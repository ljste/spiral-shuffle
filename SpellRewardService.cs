using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Services;

internal sealed class SpellRewardService(SessionActor sessionActor) : MessageService(sessionActor) {
    internal const uint JobId = 770103;
    private readonly SpellRewardQueue _rewards = new();
    private DateTime _lastPoll;
    private bool _attached;

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void StartRewards() {
        if (!RandomizerRuntime.Enabled || GetActiveWizard() is null) {
            return;
        }

        _attached = true;
        SendToSocket(new GAME_5_PROTOCOL.MSG_KILL_CLIENT_PROCESS { JobID = JobId });
        SendToSocket(new GAME_5_PROTOCOL.MSG_START_CLIENT_PROCESS {
            OwnerGID = GetActiveWizard().GameObjectID,
            JobID = JobId,
            ScriptName = "Randomizer/SpellRewards.lua"
        });
    }

    [MessageHandler(typeof(SpellDropRewards))]
    private void ReceiveRewards(SpellDropRewards rewards) {
        if (!_attached) {
            return;
        }
        var wizard = GetActiveWizard();
        uint[] learned = rewards.SpellIds.Where(id => wizard.SpellbookBehavior.HasSpell(id) &&
            RandomizerRuntime.DropSpellPool.Any(spell => spell.Variants.Contains(id))).Distinct().ToArray();
        RewardSpell[] notices = learned.Select(id => {
            var spell = (SpellTemplate)CoreObjectFactory.GetCoreTemplate(id);
            var rank = spell.m_spellRank;
            return new RewardSpell(id, spell.m_displayName, spell.m_sMagicSchoolName,
                SpellRewardQueue.PipCost(rank.m_spellRank, rank.m_xPipSpell, rank.m_shadowPips,
                    new Dictionary<string, int> {
                        ["balance"] = rank.m_balancePips, ["death"] = rank.m_deathPips, ["fire"] = rank.m_firePips,
                        ["ice"] = rank.m_icePips, ["life"] = rank.m_lifePips, ["myth"] = rank.m_mythPips,
                        ["storm"] = rank.m_stormPips
                    }),
                spell.m_accuracy, rewards.Difficulty);
        }).ToArray();
        _rewards.Enqueue(notices);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_MESSAGE_PROCESS))]
    private void ReceivePoll(GAME_5_PROTOCOL.MSG_MESSAGE_PROCESS message) {
        if (!_attached || message.JobID != JobId || !RandomizerRuntime.TryReadMenuMessage(message.Message, "SpellRewardUI", out int token)) {
            return;
        }

        if (token > 0) {
            _rewards.Acknowledge(token);
        }
        else if (DateTime.UtcNow - _lastPoll < TimeSpan.FromMilliseconds(250)) {
            return;
        }

        _lastPoll = DateTime.UtcNow;
        string payload = GetActiveWizard().IsInDuel ? "SpellRewardCombat" : _rewards.Current?.Payload ?? "SpellRewardEmpty";
        byte[] encoded = MessageEncoder.Encode(new MG2_MESSAGES_43_PROTOCOL.MSG_MG2_REWARDS { score = 0, gameName = payload });
        SendToSocket(new GAME_5_PROTOCOL.MSG_MESSAGE_PROCESS { JobID = JobId, Message = (ByteString)encoded[8..] });
    }
}
