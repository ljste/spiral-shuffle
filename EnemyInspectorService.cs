using System;
using System.Buffers.Binary;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.MessageLayer;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Services;

internal sealed class EnemyInspectorService(SessionActor sessionActor) : MessageService(sessionActor) {
    internal const uint JobId = 770102;
    private DateTime _lastQuery;
    private bool _attached;

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void StartInspector() {
        if (!RandomizerRuntime.Enabled || GetActiveWizard() is null) {
            return;
        }

        _attached = true;
        SendToSocket(new GAME_5_PROTOCOL.MSG_KILL_CLIENT_PROCESS { JobID = JobId });
        SendToSocket(new GAME_5_PROTOCOL.MSG_START_CLIENT_PROCESS {
            OwnerGID = GetActiveWizard().GameObjectID,
            JobID = JobId,
            ScriptName = "Randomizer/EnemyInspector.lua"
        });
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_MESSAGE_PROCESS))]
    private void ReceiveInspectorRequest(GAME_5_PROTOCOL.MSG_MESSAGE_PROCESS message) {
        if (!_attached || !RandomizerRuntime.Enabled || message.JobID != JobId) {
            return;
        }

        byte[] raw = message.Message;
        var request = new MG2_MESSAGES_43_PROTOCOL.MSG_MG2_REWARDS();
        if (raw.Length < 4 || raw[0] != request.ServiceId || raw[1] != request.MessageOrder) {
            return;
        }

        int length = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(2));
        if (length < 4 || length > raw.Length) {
            return;
        }

        request.Decode(new BitReader(raw[4..length]));
        string payload = request.gameName;
        if (payload == "Inspect" && DateTime.UtcNow - _lastQuery >= TimeSpan.FromMilliseconds(450)) {
            _lastQuery = DateTime.UtcNow;
            SessionActor.GetZoneActor()?.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Targets = ZoneBroadcastTarget.Sigils,
                Messages = [new EnemyInspectorQuery(GetActiveWizard().GameObjectID,
                    SpellDrops.GetSetting(GetActiveWizard().AccountId))]
            }, Self);
        }
    }

    [MessageHandler(typeof(EnemyInspectorSnapshot))]
    private void SendSnapshot(EnemyInspectorSnapshot snapshot) {
        if (!_attached) {
            return;
        }
        byte[] encoded = MessageEncoder.Encode(new MG2_MESSAGES_43_PROTOCOL.MSG_MG2_REWARDS {
            score = 0, gameName = snapshot.Payload
        });
        SendToSocket(new GAME_5_PROTOCOL.MSG_MESSAGE_PROCESS { JobID = JobId, Message = (ByteString)encoded[8..] });
    }
}
