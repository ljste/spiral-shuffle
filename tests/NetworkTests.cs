using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Configuration;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Wad;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using Type = System.Type;

internal static class NetworkTests {
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int _checks;

    internal static int Run(Assembly assembly) {
        CheckFraming();
        CheckEarlyPolls(assembly);
        CheckActors(assembly).GetAwaiter().GetResult();
        Console.WriteLine($"Passed {_checks} network checks: split packets, large frames, combined reads, zone queues, duplicate leaves and early UI polls.");
        return _checks;
    }

    internal static int CheckAssetRetries(Assembly assembly, string root) {
        const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var templates = (System.Collections.IDictionary)typeof(CoreObjectFactory).GetField("s_templateCache", Static)!.GetValue(null)!;
        const ulong missing = 3_999_999_997;
        Require(CoreObjectFactory.GetCoreTemplate(missing) is null && !templates.Contains(missing),
            "A failed template lookup was cached permanently.");
        Type loader = assembly.GetType("Imlight.CoreLib.Shared.Resources.RootArchiveLoader")!;
        var archives = (System.Collections.IDictionary)loader.GetField("s_namedWads", Static)!.GetValue(null)!;
        string name = "retry-test-" + Guid.NewGuid().ToString("N");
        MethodInfo load = loader.GetMethod("GetFileFromWad", Static)!.MakeGenericMethod(typeof(CoreTemplate));
        const string file = "ObjectData/Tier1/Wands/Wand-T1-016.xml";
        try {
            Require(load.Invoke(null, [name, file]) is null && !archives.Contains(name),
                "A failed archive lookup was cached permanently.");
            using FileStream stream = File.OpenRead(Path.Combine(root, ".runtime/patch/V_r806919.Wizard_1_610/Data/GameData/Root.wad"));
            archives[name] = ArchiveParser.Parse(stream);
            Require(load.Invoke(null, [name, file]) is CoreTemplate, "An unavailable asset could not recover after its archive became available.");
        }
        finally {
            archives.Remove(name);
        }
        Console.WriteLine("Passed asset retry checks: missing templates and archives recover without a restart.");
        return 3;
    }

    private static void CheckFraming() {
        byte[] first = KiPacketCodec.Encode(new LOGIN_7_PROTOCOL.MSG_SELECTCHARACTER { CharID = 123UL });
        byte[] second = KiPacketCodec.Encode(new GAME_5_PROTOCOL.MSG_ZONETRANSFERACK());
        byte[] large = KiPacketCodec.Encode(new GAME_5_PROTOCOL.MSG_NEWOBJECT { Data = (ByteString)new byte[40_000] });
        foreach (byte[] frame in new[] { first, second, large }) {
            int headerLength = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2)) == 0x8000 ? 8 : 4;
            foreach (int split in Enumerable.Range(1, Math.Min(frame.Length - 1, 256)).Concat([frame.Length - 1])) {
                KiPacketBuffer buffer = new();
                Require(buffer.Append(frame[..split], split).Count == 0, "A partial packet was decoded before its body arrived.");
                byte[][] result = buffer.Append(frame[split..], frame.Length - split).ToArray();
                Require(result.Length == 1 && result[0].SequenceEqual(frame), "A split packet lost bytes.");
                Require(KiPacketCodec.Decode(result[0]).Count == 1, "A reassembled packet could not be decoded.");
            }
            Require(frame.Length > headerLength, "The encoded packet has no body.");
        }

        byte[] stream = first.Concat(second).Concat(large).Concat(first).ToArray();
        foreach (int chunkSize in new[] { 1, 2, 3, 7, 64, 1024, 65536, stream.Length }) {
            KiPacketBuffer buffer = new();
            List<byte[]> received = [];
            for (int offset = 0; offset < stream.Length; offset += chunkSize) {
                byte[] chunk = stream[offset..Math.Min(offset + chunkSize, stream.Length)];
                received.AddRange(buffer.Append(chunk, chunk.Length));
            }
            Require(received.Count == 4 && received.SelectMany(frame => frame).SequenceEqual(stream),
                "Combined TCP reads or a fragmented header changed the packet stream.");
        }

        foreach (byte[] invalid in new byte[][] { [0, 0, 8, 0], [13, 240, 0, 0], [13, 240, 0, 128, 255, 255, 255, 127] }) {
            try {
                new KiPacketBuffer().Append(invalid, invalid.Length);
                throw new Exception("An invalid frame was accepted.");
            }
            catch (InvalidDataException) { _checks++; }
        }
    }

    private static void CheckEarlyPolls(Assembly assembly) {
        byte[] poll = MessageEncoder.Encode(new MG2_MESSAGES_43_PROTOCOL.MSG_MG2_REWARDS { gameName = "SpellRewardUI", score = 0 });
        Type rewards = assembly.GetType("Imlight.CoreLib.Game.Services.SpellRewardService")!;
        object service = RuntimeHelpers.GetUninitializedObject(rewards);
        rewards.GetMethod("ReceivePoll", Instance)!.Invoke(service,
            [new GAME_5_PROTOCOL.MSG_MESSAGE_PROCESS { JobID = 770103, Message = (ByteString)poll[8..] }]);
        Type inspector = assembly.GetType("Imlight.CoreLib.Game.Services.EnemyInspectorService")!;
        inspector.GetMethod("ReceiveInspectorRequest", Instance)!.Invoke(RuntimeHelpers.GetUninitializedObject(inspector),
            [new GAME_5_PROTOCOL.MSG_MESSAGE_PROCESS { JobID = 770102, Message = (ByteString)poll[8..] }]);
        _checks += 2;
    }

    private static async Task CheckActors(Assembly assembly) {
        using ActorSystem system = ActorSystem.Create("NetworkRegression", ConfigurationFactory.ParseString("akka.loglevel = ERROR"));
        Channel<object> received = Channel.CreateUnbounded<object>();
        IActorRef capture = system.ActorOf(Props.Create(() => new Capture(received.Writer)));
        try {
            await CheckSocket(system, capture, received.Reader, assembly);
            IActorRef zone = system.ActorOf(Props.Create(() => new TestZone()));
            IActorRef first = system.ActorOf(Props.Create(() => new Capture(Channel.CreateUnbounded<object>().Writer)));
            IActorRef second = system.ActorOf(Props.Create(() => new Capture(Channel.CreateUnbounded<object>().Writer)));
            var add = new ZONE_102_PROTOCOL.MSG_ADDPLAYER { PlayerActor = first, PlayerObject = new WizClientObject(), Wizard = new Wizard() };
            Task<ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP> joining = zone.Ask<ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP>(add, TimeSpan.FromSeconds(5));
            await zone.Ask<int>(new PendingCount());
            zone.Tell(new FinishLoading());
            Require((await joining).WizardGameObject == add.PlayerObject, "A queued join replied to the player instead of the waiting request.");
            await zone.Ask<ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP>(add);
            await zone.Ask<ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP>(new ZONE_102_PROTOCOL.MSG_ADDPLAYER {
                PlayerActor = second, PlayerObject = new WizClientObject(), Wizard = new Wizard()
            });
            Require(await zone.Ask<int>(new MemberCount()) == 2, "A duplicate join inflated the player count.");
            for (int index = 0; index < 2; index++) {
                await zone.Ask<ZONE_102_PROTOCOL.MSG_REMOVEPLAYERRSP>(new ZONE_102_PROTOCOL.MSG_REMOVEPLAYER { PlayerActor = first });
            }
            Require(await zone.Ask<int>(new MemberCount()) == 1, "A duplicate leave made an occupied zone appear empty.");
            CheckWorldQueue(assembly, capture);
            CheckStaleInstance(assembly, first, second);
            await CheckWarpHandoff(system, assembly);
            await CheckLogoutFlush(system, capture, assembly);
        }
        finally {
            await system.Terminate().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task CheckSocket(ActorSystem system, IActorRef capture, ChannelReader<object> reader, Assembly assembly) {
        using Socket acceptor = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        acceptor.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        acceptor.Listen(1);
        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        Task connect = client.ConnectAsync(acceptor.LocalEndPoint!);
        using Socket server = await acceptor.AcceptAsync();
        await connect;
        Type listener = assembly.GetType("Imlight.CoreLib.Shared.Networking.SocketListener")!;
        IActorRef actor = system.ActorOf(Props.Create(listener, capture, server, (ushort)123));
        byte[] packet = MessageEncoder.Encode(new LOGIN_7_PROTOCOL.MSG_SELECTCHARACTER { CharID = 987UL });
        foreach (byte value in packet) {
            await client.SendAsync(new byte[] { value }, SocketFlags.None);
        }
        var message = (SERVER_100_PROTOCOL.MSG_RECEIVEDPACKET)await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Require(message.Packet is LOGIN_7_PROTOCOL.MSG_SELECTCHARACTER select && select.CharID == 987UL,
            "The actual socket listener lost a fragmented character selection packet.");
        GAME_5_PROTOCOL.MSG_NEWOBJECT outgoing = new() { Data = (ByteString)new byte[40_000] };
        byte[] expected = KiPacketCodec.Encode(outgoing);
        Type sender = assembly.GetType("Imlight.CoreLib.Shared.Networking.SocketSender")!;
        server.SendBufferSize = 512;
        IActorRef senderActor = system.ActorOf(Props.Create(sender, capture, server, (ushort)123));
        senderActor.Tell(outgoing);
        byte[] actual = new byte[expected.Length];
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        for (int offset = 0; offset < actual.Length;) {
            int count = await client.ReceiveAsync(actual.AsMemory(offset), SocketFlags.None, timeout.Token);
            Require(count > 0, "The sender closed before completing the object packet.");
            offset += count;
        }
        Require(actual.SequenceEqual(expected), "The actual socket sender truncated or misframed a large object.");
        system.Stop(senderActor);
        system.Stop(actor);
    }

    private static void CheckWorldQueue(Assembly assembly, IActorRef target) {
        Type worldType = assembly.GetType("Imlight.CoreLib.Game.World.GameWorld")!;
        object world = RuntimeHelpers.GetUninitializedObject(worldType);
        Dictionary<ZONE_102_PROTOCOL.MSG_ZONETRANSFER, IActorRef> pending = [];
        for (int index = 0; index < 3; index++) {
            pending.Add(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER { DestinationZone = "WizardCity/WC_Hub" }, target);
        }
        worldType.GetField("_awaitingTransfers", Instance)!.SetValue(world, pending);
        worldType.GetField("_publicZones", Instance)!.SetValue(world, new Dictionary<string, IActorRef> { ["WizardCity/WC_Hub"] = target });
        worldType.GetMethod("ProcessTransfersForPublicZone", Instance)!.Invoke(world, ["WizardCity/WC_Hub"]);
        Require(pending.Count == 0, "Simultaneous zone entrants left transfers queued.");
    }

    private static void CheckStaleInstance(Assembly assembly, IActorRef oldZone, IActorRef replacement) {
        Type type = assembly.GetType("Imlight.CoreLib.Game.World.InstanceContainer")!;
        object container = RuntimeHelpers.GetUninitializedObject(type);
        Dictionary<string, IActorRef> zones = new() { ["WizardCity/Interiors/WC_RattlebonesTower"] = replacement };
        type.GetField("_zones", Instance)!.SetValue(container, zones);
        type.GetField("_questProgress", Instance)!.SetValue(container, new InstanceQuestProgress());
        MethodInfo remove = type.GetMethod("RemoveClosedZone", Instance)!;
        remove.Invoke(container, [oldZone]);
        Require(zones.Count == 1, "An old instance's close removed its replacement.");
        remove.Invoke(container, [replacement]);
        Require(zones.Count == 0, "A closed instance remained available for transfers.");
    }

    private static async Task CheckWarpHandoff(ActorSystem system, Assembly assembly) {
        Type type = assembly.GetType("Imlight.CoreLib.Game.Services.ZoneService")!;
        IActorRef service = system.ActorOf(Props.Create(type, new object[] { null! }));
        Type identity = assembly.GetType("Imlight.CoreLib.Shared.Packets.SERVICE_101_PROTOCOL+MSG_QUERYMESSAGESERVICEIDENTITY")!;
        object before = await service.Ask<object>(Activator.CreateInstance(identity)!);
        object beforeService = before.GetType().GetField("Service")!.GetValue(before)!;
        FieldInfo zoneField = type.GetField("ZoneActor", Instance)!;
        Require(zoneField.GetValue(beforeService) is null, "The handoff fixture still has a zone attached.");
        for (int index = 0; index < 5; index++) {
            service.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Message = new GAME_5_PROTOCOL.MSG_ZONETRANSFERACK()
            });
            var reply = await service.Ask<ZONE_102_PROTOCOL.MSG_QUERYZONEENTITYRSP>(
                new ZONE_102_PROTOCOL.MSG_QUERYZONEENTITY { GlobalID = 1 }, TimeSpan.FromSeconds(3));
            Require(reply.ZoneObject is null, "A detached warp session returned an object from its old zone.");
        }
        object after = await service.Ask<object>(Activator.CreateInstance(identity)!);
        Require(ReferenceEquals(beforeService, after.GetType().GetField("Service")!.GetValue(after)),
            "Late zone messages restarted the service during a warp.");
    }

    private static async Task CheckLogoutFlush(ActorSystem system, IActorRef capture, Assembly assembly) {
        Type list = assembly.GetType("Imlight.CoreLib.Shared.Packets.SERVICE_101_PROTOCOL+MSG_SERVICESLIST")!;
        object response = Activator.CreateInstance(list)!;
        list.GetField("Services")!.SetValue(response, new List<Type>());
        IActorRef factory = system.ActorOf(Props.Create(() => new EmptyServices(response)));
        using Socket acceptor = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        acceptor.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        acceptor.Listen(1);
        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        Task connecting = client.ConnectAsync(acceptor.LocalEndPoint!);
        using Socket server = await acceptor.AcceptAsync();
        server.SendBufferSize = 512;
        server.NoDelay = true;
        await connecting;
        IActorRef session = system.ActorOf(SessionActor.Props(server, 124, capture, factory));
        for (int index = 0; index < 8; index++) {
            session.Tell(new GAME_5_PROTOCOL.MSG_NEWOBJECT { Data = (ByteString)new byte[60_000] });
        }
        session.Tell(new GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT());
        session.Tell("Close");
        await Task.Delay(50);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        KiPacketBuffer frames = new();
        List<IMessage> messages = [];
        byte[] buffer = new byte[4096];
        try {
            while (true) {
                int count = await client.ReceiveAsync(buffer, SocketFlags.None, timeout.Token);
                if (count == 0) { break; }
                messages.AddRange(frames.Append(buffer, count).SelectMany(KiPacketCodec.Decode));
            }
        }
        catch (SocketException ex) {
            throw new InvalidDataException($"Logout socket reset after receiving {messages.Count} of 9 messages.", ex);
        }
        Require(messages.Count == 9 && messages.Take(8).All(message => message is GAME_5_PROTOCOL.MSG_NEWOBJECT),
            "Session shutdown discarded queued data before the logout reply.");
        Require(messages[^1] is GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT,
            "The socket closed without delivering the final logout acknowledgement.");
    }

    private sealed class EmptyServices : ReceiveActor {
        public EmptyServices(object response) {
            Receive<object>(_ => Sender.Tell(response));
        }
    }

    private sealed class Capture : ReceiveActor {
        public Capture(ChannelWriter<object> writer) {
            Receive<string>(message => message == "Close", _ => Sender.Tell("Closed"));
            Receive<object>(message => writer.TryWrite(message));
        }
    }

    private sealed class FinishLoading;
    private sealed class MemberCount;
    private sealed class PendingCount;

    private sealed class TestZone : Zone {
        public TestZone() : base("WizardCity/WC_Hub", 1, false) {
            typeof(Zone).GetField("_supervisors", Instance)!.SetValue(this, new List<IActorRef>());
        }

        [MessageHandler(typeof(FinishLoading))]
        private void Finish() {
            typeof(Zone).GetField("_isLoading", Instance)!.SetValue(this, false);
            typeof(Zone).GetMethod("ProcessQueue", Instance)!.Invoke(this, null);
        }

        [MessageHandler(typeof(MemberCount))]
        private void Members() => Sender.Tell(typeof(Zone).GetField("_playerCount", Instance)!.GetValue(this)!);

        [MessageHandler(typeof(PendingCount))]
        private void Pending() => Sender.Tell(0);
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
        _checks++;
    }
}
