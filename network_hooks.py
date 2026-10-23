"""Keep socket framing and zone lifecycle intact across reconnects."""
from pathlib import Path


def replace(source: Path, relative: str, before: str, after: str) -> None:
    path = source / relative
    content = path.read_text(encoding="utf-8-sig")
    if content.count(before) != 1:
        raise ValueError(f"Expected one network hook in {relative}: {before[:70]}")
    path.write_text(content.replace(before, after), encoding="utf-8")


def patch_sockets(source: Path) -> None:
    listener = "Shared/Networking/SocketListener.cs"
    replace(source, listener, "SocketListener : ReceiveActor, IDisposable {", "SocketListener : ReceiveActor, IDisposable, IWithTimers {\n    public ITimerScheduler Timers { get; set; }\n    private readonly KiPacketBuffer _frames = new();")
    replace(source, listener, """    private void OnSocketReadCompleted(SocketReadCompleted result) {
        ProcessReceivedData(result.Buffer, result.ByteCount);
    }""", """    private void OnSocketReadCompleted(SocketReadCompleted result) {
        if (_isDisposed) {
            return;
        }
        if (result.ByteCount > 0 && !_tokenBucket.TryAcquire()) {
            Timers.StartSingleTimer("receive-resume", result,
                TimeSpan.FromSeconds(1.0 / Math.Max(1, _tokenBucketPerSecond)));
            return;
        }
        ProcessReceivedData(result.Buffer, result.ByteCount);
    }""")
    path = source / listener
    content = path.read_text(encoding="utf-8")
    start = content.index("            if (!_tokenBucket.TryAcquire()) {")
    end = content.index("            foreach (var packet in packets)", start)
    content = content[:start] + """            var packets = _frames.Append(buffer, bytesReceived)
                .SelectMany(KiPacketCodec.Decode);

""" + content[end:]
    start = content.index("    private IMessage[] GetPacketsFromBuffer(")
    end = content.index("    private void LogReceivedPacket(", start)
    path.write_text(content[:start] + content[end:], encoding="utf-8")
    sender = "Shared/Networking/SocketSender.cs"
    replace(source, sender, "var data = MessageEncoder.Encode(message);", "var data = KiPacketCodec.Encode(message);")
    replace(source, sender, "        Receive<IMessage>(SendToSocket);", """        Receive<IMessage>(SendToSocket);
        Receive<FlushSocket>(_ => {
            try {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            Sender.Tell(new SocketFlushed());
        });""")
    for path in [listener, sender]:
        replace(source, path, '_sessionActorRef.Ask("Close");', '_sessionActorRef.Tell("Close");')
    replace(source, sender, """            var bytesSent = _socket.Send(data);
            if (bytesSent != data.Length) {
                throw new SessionFatalException(
                    $"SessionActor [{_sessionid}] send failure: " +
                    $"Sent {bytesSent} bytes out of {data.Length} bytes.");
            }""", """            int offset = 0;
            while (offset < data.Length) {
                int sent = _socket.Send(data.AsSpan(offset), SocketFlags.None);
                if (sent == 0) {
                    throw new SessionFatalException($"SessionActor [{_sessionid}] socket closed during send.");
                }
                offset += sent;
            }""")


def patch_zone_service(source: Path) -> None:
    path = "Game/Services/ZoneService.cs"
    replace(source, path, """        if (_isTransferQueued) {
            return;
        }

        // Sending the server transfer""", """        if (_isTransferQueued) {
            Sender.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP {
                ErrorCode = 1, ErrorMessage = "A zone transfer is already in progress."
            });
            return;
        }
        if (message.OwnerCharId == 0) {
            message.OwnerCharId = GetActiveWizard().CharId;
        }

        // Sending the server transfer""")
    replace(source, path, """                DoTeleport(message.DestinationLocation);

                return;""", """                Sender.Tell(zoneDetails);
                DoTeleport(Util.GetCompactStringFromVector(zoneDetails.Location,
                    new Imcodec.Math.Vector3(0, 0, zoneDetails.Orientation)));
                return;""")
    replace(source, path, """    private void DoZoneTransfer() {
        // Remove the player""", """    private void DoZoneTransfer() {
        if (!_isTransferQueued || ZoneActor is null) {
            return;
        }

        // Remove the player""")
    replace(source, path, """        var zoneDetails = AskServer<ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP>(message);""", """        ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP zoneDetails;
        try {
            zoneDetails = AskServer<ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP>(message);
        }
        catch (Exception ex) {
            zoneDetails = new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP {
                ErrorCode = 1, ErrorMessage = "The destination zone did not become ready. Please try again."
            };
            Logger.Warning("Zone transfer failed for {Zone}: {Reason}", Logger.Args(message.DestinationZone, ex.Message));
        }""")
    replace(source, path, """        _isTransferQueued = true;

        // Ask the client""", """        _isTransferQueued = true;
        Timers.StartSingleTimer("zone-transfer-ack", new TransferAckExpired(), TimeSpan.FromSeconds(30));

        // Ask the client""")
    replace(source, path, """    private void DoZoneTransfer() {
        if (!_isTransferQueued || ZoneActor is null)""", """    private sealed class TransferAckExpired;

    [MessageHandler(typeof(TransferAckExpired))]
    private void ReceiveTransferAckExpired(TransferAckExpired message) {
        if (_isTransferQueued && ZoneActor is not null) {
            _isTransferQueued = false;
            InformGameClient("The zone transfer timed out. You can try the entrance again.");
        }
    }

    private void DoZoneTransfer() {
        Timers.Cancel("zone-transfer-ack");
        if (!_isTransferQueued || ZoneActor is null)""")
    replace(source, path, """            _ = ZoneActor.Ask<ZONE_102_PROTOCOL.MSG_REMOVEPLAYERRSP>(removePlayerMsg, _zoneRemovalWaitTime).Result;

            // Remove""", """            _ = ZoneActor.Ask<ZONE_102_PROTOCOL.MSG_REMOVEPLAYERRSP>(removePlayerMsg, _zoneRemovalWaitTime).Result;
            ZoneActor = null;

            // Remove""")
    replace(source, path, """    private void ReceiveZoneBroadcast(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST message) {
        if (ZoneActor is null) {
            throw new Exception("Zone Reference was null.");
        }""", """    private void ReceiveZoneBroadcast(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST message) {
        if (ZoneActor is null) {
            return;
        }""")
    replace(source, path, """    private void ReceiveQueryZoneObject(ZONE_102_PROTOCOL.MSG_QUERYZONEENTITY message) {
        if (ZoneActor is null) {
            throw new Exception("Zone Reference was null.");
        }""", """    private void ReceiveQueryZoneObject(ZONE_102_PROTOCOL.MSG_QUERYZONEENTITY message) {
        if (ZoneActor is null) {
            Sender.Tell(new ZONE_102_PROTOCOL.MSG_QUERYZONEENTITYRSP());
            return;
        }""")


def patch_zone_queue(source: Path) -> None:
    path = "Game/Zone/Core/Zone.cs"
    replace(source, path, "private readonly Dictionary<IActorRef, IServerMessage> _pendingPlayerEvents = [];", """private readonly List<(IActorRef ReplyTo, IServerMessage Message)> _pendingPlayerEvents = [];
    private readonly HashSet<IActorRef> _members = [];""")
    replace(source, path, "_pendingPlayerEvents[Sender] = message;", "_pendingPlayerEvents.Add((Sender, message));")
    file = source / path
    content = file.read_text(encoding="utf-8")
    content = content.replace("_pendingPlayerEvents[message.PlayerActor] = message;", "_pendingPlayerEvents.Add((Sender, message));")
    file.write_text(content, encoding="utf-8")
    replace(source, path, """        _playerCount++;
        Timers.Cancel(IDLE_EXPIRE_TIMER);""", """        if (!_members.Add(message.PlayerActor)) {
            Sender.Tell(new ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP { WizardGameObject = message.PlayerObject });
            return;
        }
        _playerCount = _members.Count;
        Timers.Cancel(IDLE_EXPIRE_TIMER);""")
    replace(source, path, "_pendingPlayerEvents.Remove(message.PlayerActor);", """_pendingPlayerEvents.RemoveAll(entry => entry.Message switch {
                ZONE_102_PROTOCOL.MSG_ADDPLAYER add => add.PlayerActor.Equals(message.PlayerActor),
                ZONE_102_PROTOCOL.MSG_PLAYERMOVE move => move.PlayerActor.Equals(message.PlayerActor),
                _ => false
            });""")
    replace(source, path, """        if (_playerCount > 0) {
            _playerCount--;
        }

        InformZoneSupervisors(message.PlayerActor, message);""", """        if (!_members.Remove(message.PlayerActor)) {
            Sender.Tell(new ZONE_102_PROTOCOL.MSG_REMOVEPLAYERRSP());
            return;
        }
        _playerCount = _members.Count;

        InformZoneSupervisors(message.PlayerActor, message);""")
    replace(source, path, """                _playerCount++;
                Timers.Cancel(IDLE_EXPIRE_TIMER);
                InformZoneSupervisors(playerActor, addPlayer);""", """                if (!_members.Add(addPlayer.PlayerActor)) {
                    playerActor.Tell(new ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP { WizardGameObject = addPlayer.PlayerObject });
                    continue;
                }
                _playerCount = _members.Count;
                Timers.Cancel(IDLE_EXPIRE_TIMER);
                InformZoneSupervisors(addPlayer.PlayerActor, addPlayer);""")
    replace(source, path, "InformZoneSupervisors(playerActor, pendingEvent);", """if (pendingEvent is ZONE_102_PROTOCOL.MSG_PLAYERMOVE move) {
                    InformZoneSupervisors(move.PlayerActor, pendingEvent);
                }""")
    replace(source, path, """        foreach (var (player, _) in _pendingPlayerEvents) {
            var rsp""", """        foreach (var (player, pending) in _pendingPlayerEvents) {
            if (pending is ZONE_102_PROTOCOL.MSG_ADDPLAYER) {
                player.Tell(new ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP());
                continue;
            }
            if (pending is not ZONE_102_PROTOCOL.MSG_ZONETRANSFER) {
                continue;
            }
            var rsp""")
    replace(source, path, "MobileId = GenerateObjectIdentifier(),", "MobileId = message.SendToClient ? (ushort)0 : GenerateObjectIdentifier(),")
    replace(source, path, """        ZoneData = message.ZoneData;

        // Inform""", """        ZoneData = message.ZoneData;
        Timers.StartSingleTimer("zone-ready", new ZONE_102_PROTOCOL.MSG_ZONELOADTIMER(), TimeSpan.FromSeconds(10));

        // Inform""")
    replace(source, path, """        if (_supervisorLoadResults.All(x => x.Value)) {
            // Finally""", """        if (_supervisorLoadResults.All(x => x.Value)) {
            Timers.Cancel("zone-ready");
            // Finally""")


def patch_world_lifecycle(source: Path) -> None:
    world = "Game/World/GameWorld.cs"
    file = source / world
    content = file.read_text(encoding="utf-8")
    content = content.replace("_awaitingTransfers.Where(t => t.Key.DestinationZone == message.ZonePath);", "_awaitingTransfers.Where(t => t.Key.DestinationZone == message.ZonePath).ToArray();")
    content = content.replace("_awaitingTransfers.Where(t => t.Key.DestinationZone == zonePath);", "_awaitingTransfers.Where(t => t.Key.DestinationZone == zonePath).ToArray();")
    file.write_text(content, encoding="utf-8")
    replace(source, world, """        // If the zone failed to load, inform anyone waiting for the zone to load.
        if (message.Error)""", """        if (!_zoneLoaderActors.TryGetValue(message.ZonePath, out var loader) || !loader.Equals(Sender)) {
            return;
        }

        // If the zone failed to load, inform anyone waiting for the zone to load.
        if (message.Error)""")
    replace(source, world, """        // Stop the timer.
        var loadZoneTimeoutKey""", """        _instanceCreationCalledByMap.Remove(zonePath);

        // Stop the timer.
        var loadZoneTimeoutKey""")
    replace(source, world, """            _zoneLoaderActors.Remove(message.ZonePath);
            Context.Stop(loaderRef);""", """            RemoveZoneLoader(message.ZonePath);""")
    replace(source, world, """        _dynamicZoneIds.Remove(dynamicId);
    }""", """        _dynamicZoneIds.Remove(dynamicId);
        foreach (var entry in _publicZones.Where(entry => entry.Value.Equals(Sender)).ToArray()) {
            _publicZones.Remove(entry.Key);
        }
    }""")
    replace(source, world, "var zoneActorName = SanitizeZoneName(zoneName);", "var zoneActorName = $" + '"{SanitizeZoneName(zoneName)}-{Guid.NewGuid():N}";')
    replace(source, world, """        // Log the new zone creation.
        Logger.Information""", """        Context.Watch(zone);

        // Log the new zone creation.
        Logger.Information""")
    replace(source, world, """    private static string SanitizeZoneName(string zoneName)""", """    [MessageHandler(typeof(Terminated))]
    private void ReceiveTerminated(Terminated message) {
        foreach (var entry in _publicZones.Where(entry => entry.Value.Equals(message.ActorRef)).ToArray()) {
            _publicZones.Remove(entry.Key);
        }
    }

    private static string SanitizeZoneName(string zoneName)""")
    container = "Game/World/InstanceContainer.cs"
    replace(source, container, "using System.Collections.Generic;", "using System.Collections.Generic;\nusing System.Linq;")
    replace(source, container, """        if (!_zones.ContainsKey(message.DestinationZone)) {
            throw new Exception($"Zone {message.DestinationZone} not found in instance container {_instanceOwnerId}");
        }""", """        if (!_zones.ContainsKey(message.DestinationZone)) {
            Context.Parent.Forward(message);
            return;
        }""")
    replace(source, container, """        if (!_zones.Remove(message.ZoneName, out var zoneActor)) {
            return;
        }""", """        if (!_zones.TryGetValue(message.ZoneName, out var zoneActor) ||
            (!Sender.Equals(Context.Parent) && !Sender.Equals(zoneActor))) {
            return;
        }
        _zones.Remove(message.ZoneName);""")
    replace(source, container, "var zoneActorName = SanitizeZoneName(zoneName);", "var zoneActorName = $" + '"{SanitizeZoneName(zoneName)}-{Guid.NewGuid():N}";')
    replace(source, container, """        // Log the new zone creation.
        Logger.Information""", """        Context.Watch(zone);

        // Log the new zone creation.
        Logger.Information""")
    replace(source, container, """    private IActorRef CreateZone(string zoneName) {""", """    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONECLOSED))]
    private void ReceiveZoneClosed(ZONE_102_PROTOCOL.MSG_ZONECLOSED message) {
        RemoveClosedZone(Sender);
        Context.Parent.Forward(message);
    }

    [MessageHandler(typeof(Terminated))]
    private void ReceiveTerminated(Terminated message) => RemoveClosedZone(message.ActorRef);

    private void RemoveClosedZone(IActorRef zone) {
        foreach (var entry in _zones.Where(entry => entry.Value.Equals(zone)).ToArray()) {
            _zones.Remove(entry.Key);
            _questProgress.RemoveZone(entry.Key);
        }
    }

    private IActorRef CreateZone(string zoneName) {""")


def patch_network(source: Path) -> None:
    patch_sockets(source)
    patch_zone_service(source)
    patch_zone_queue(source)
    patch_world_lifecycle(source)
    patch_asset_retries(source)
    session = "Shared/Networking/SessionActor.cs"
    replace(source, session, "    private bool _isDisposed;", "    private bool _isDisposed;\n    private ICancelable _socketCloseTimeout;")
    replace(source, session, """        // Dispose self.
        ActorRef.Tell(PoisonPill.Instance);""", """        _socketCloseTimeout = Context.System.Scheduler.ScheduleTellOnceCancelable(
            TimeSpan.FromSeconds(5), Self, new SocketFlushed(), Self);
        _socketSenderRef.Tell(new FlushSocket(), Self);""")
    replace(source, session, """    private void SendToSocket(IMessage message) {
        _socketSenderRef.Forward(message);
    }""", """    private void SendToSocket(IMessage message) {
        if (!_isDisposed) {
            _socketSenderRef.Forward(message);
        }
    }""")
    replace(source, session, """    private void ConfigureReceivers() {
        // Specific message handlers.""", """    private void FinishSocketClose(SocketFlushed message) {
        if (_isDisposed && (Sender.Equals(_socketSenderRef) || Sender.Equals(Self))) {
            _socketCloseTimeout?.Cancel();
            Context.Stop(Self);
        }
    }

    private void ConfigureReceivers() {
        Receive<SocketFlushed>(FinishSocketClose);
        // Specific message handlers.""")
    replace(source, session, "return handler.Ask<T>(msg, timeout: TimeSpan.FromSeconds(20)).Result;", """var timeout = TimeSpan.FromSeconds(msg is ZONE_102_PROTOCOL.MSG_ZONETRANSFER ? 40 : 20);
                    return handler.Ask<T>(msg, timeout).Result;""")
    replace(source, session, "return ServerRef.Ask<T>(msg).Result;", """return msg is ZONE_102_PROTOCOL.MSG_ZONETRANSFER
                ? ServerRef.Ask<T>(msg, TimeSpan.FromSeconds(35)).Result
                : ServerRef.Ask<T>(msg).Result;""")


def patch_asset_retries(source: Path) -> None:
    path = "Shared/Resources/RootArchiveLoader.cs"
    replace(source, path, """            if (!s_namedWads.TryGetValue(wadName, out var wad)) {
                wad = LoadNamedWad(wadName);
                s_namedWads[wadName] = wad;
            }""", """            if (!s_namedWads.TryGetValue(wadName, out var wad) || wad is null) {
                wad = LoadNamedWad(wadName);
                if (wad is not null) {
                    s_namedWads[wadName] = wad;
                }
            }""")
    path = "Shared/Resources/CoreObjectFactory.cs"
    replace(source, path, """        // Slow path: load from disk.
        return s_templateCache.GetOrAdd(id, _ => {
            if (!TemplateLocations().TryGetValue(id, out var templateLocation)) {
                Logger.Error("Could not find CoreTemplate by ID {Tid}. Finding the template failed.",\x20
                    Logger.Args(id));
                return null;
            }

            var templateObj = LoadTemplateFile(templateLocation.m_filename);
            if (templateObj is null) {
                Logger.Error("Could not load CoreTemplate from {Loc}. Could not get file from its archive.",
                    Logger.Args(templateLocation.m_filename));
            }

            return templateObj;
        });""", """        if (id == 0 || !TemplateLocations().TryGetValue(id, out var templateLocation)) {
            return null;
        }

        var template = LoadTemplateFile(templateLocation.m_filename);
        if (template is null) {
            Logger.Warning("Template {TemplateId} is unavailable and can be retried.", Logger.Args(id));
            return null;
        }
        return s_templateCache.GetOrAdd(id, template);""")
    path = "Game/Zone/Core/ZoneLoader.cs"
    replace(source, path, "            ZoneDataFileCache.Add(message.ZonePath, files);\n", "")
    replace(source, path, """            Sender.Tell(completionMsg);
        } catch""", """            if (!error && !fromCache) {
                ZoneDataFileCache.Add(message.ZonePath, files);
            }
            Sender.Tell(completionMsg);
        } catch""")
