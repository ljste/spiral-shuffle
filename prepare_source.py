"""Prepare an isolated server build with the native randomizer menu hooks."""
import shutil
from pathlib import Path

from vendor_hooks import patch_vendor_hooks
from spell_drop_hooks import patch_spell_drops
from item_card_hooks import patch_item_cards
from mana_hooks import patch_infinite_mana
from spell_number_hooks import patch_spell_numbers
from creature_ai_hooks import patch_creature_ai
from trainer_hooks import patch_trainers
from qa_gear_hooks import patch_qa_gear
from network_hooks import patch_network


ROOT = Path(__file__).resolve().parent
ORIGINAL = ROOT / ".build/upstream/Imlight-b07987168fcfa2d123a5d78df90ca0c76405e427/src/Imlight.CoreLib"
SOURCE = ROOT / ".build/source"


def replace(relative: str, before: str, after: str) -> None:
    path = SOURCE / relative
    content = path.read_text(encoding="utf-8-sig")
    if content.count(before) != 1:
        raise ValueError(f"Expected exactly one hook in {relative}: {before[:60]}")
    path.write_text(content.replace(before, after).replace("\u2014", ","), encoding="utf-8")


def patch_quest_flow() -> None:
    replace("Game/Services/QuestService.cs", """    private void SendQuestResumeMessage(QuestTemplate qTemplate, QuestInstance qInstance) {
        var qMadLibs""", """    private void SendQuestResumeMessage(QuestTemplate qTemplate, QuestInstance qInstance) {
        if (qTemplate.m_isHidden) {
            return;
        }

        var qMadLibs""")
    replace("Game/Services/QuestService.cs", """        SendToSocket(qSendMsg);
        ShowQuestStartDialogue(qTemplate);""", """        if (!qTemplate.m_isHidden) {
            SendToSocket(qSendMsg);
        }
        ShowQuestStartDialogue(qTemplate);""")
    replace("Game/Services/QuestService.cs", """    private void SendGoalMessage(GoalTemplate gTemplate, QuestInstance qInstance, byte sendType = 0, bool forceSendDestZone = true) {
        var gInstance""", """    private void SendGoalMessage(GoalTemplate gTemplate, QuestInstance qInstance, byte sendType = 0, bool forceSendDestZone = true) {
        if (GetQuestTemplate(qInstance.QuestName)?.m_isHidden == true) {
            return;
        }

        var gInstance""")
    replace("Game/Services/TutorialService.cs", """        if (!IsTutorialZone(wizard.Zone)) {
            return;
        }

        if (!_serializer.Serialize(_tutorialInfo,""", """        if (!IsTutorialZone(wizard.Zone)) {
            if (wizard.HasQuest(TUTORIAL_INTRO_QUEST_NAME)) {
                CompleteTutorialIntro(wizard, GetActiveGameObject());
            }

            return;
        }

        if (!_serializer.Serialize(_tutorialInfo,""")
    replace("Game/Services/TutorialService.cs", """            RemoveControlQuests(wizard);
            CompleteTutorialIntro(wizard, playerObj);
            EquipStarterWandAndDeck(wizard);""", """            if (!CompleteTutorialIntro(wizard, playerObj)) {
                return false;
            }

            RemoveControlQuests(wizard);
            EquipStarterWandAndDeck(wizard);""")
    replace("Game/Services/TutorialService.cs", """        if (goalName == "Teleport") {
            RemoveControlQuests(wizard);""", """        if (goalName == "Teleport") {
            if (!CompleteTutorialIntro(wizard, playerObj)) {
                return false;
            }

            RemoveControlQuests(wizard);""")
    replace("Game/Services/TutorialService.cs", """    private bool CompleteTutorialIntro(Wizard wizard, CoreObject playerObj) {
        // Skippers""", """    private bool CompleteTutorialIntro(Wizard wizard, CoreObject playerObj) {
        if (wizard.HasCompletedQuest(TUTORIAL_INTRO_QUEST_NAME)) {
            if (wizard.HasQuest(TUTORIAL_INTRO_QUEST_NAME)) {
                RemoveQuestAndClearFromJournal(wizard, TUTORIAL_INTRO_QUEST_NAME);
            }

            return true;
        }

        // Skippers""")
    replace("Game/Services/TutorialService.cs", """        var removeSuccess = wizard.CompleteQuestGoal(TUTORIAL_INTRO_QUEST_NAME, TUTORIAL_INTRO_GOAL_NAME);

        var questTemplate""", """        var questTemplate""")
    replace("Game/Services/TutorialService.cs", """        ResultDispatcher.ExecuteResults(
            actorContext: Context,
            results: goalTemplate.m_completeResults,
            playerRef: SessionActor.ActorRef,
            playerObj: playerObj,
            questName: TUTORIAL_INTRO_QUEST_NAME,
            goalName: TUTORIAL_INTRO_GOAL_NAME,
            zoneActor: SessionActor.GetZoneActor()
        );

        return removeSuccess;""", """        if (!goalInstance.IsGoalCompleted()) {
            if (!wizard.CompleteQuestGoal(TUTORIAL_INTRO_QUEST_NAME, TUTORIAL_INTRO_GOAL_NAME)) {
                return false;
            }

            ResultDispatcher.ExecuteResults(
                actorContext: Context,
                results: goalTemplate.m_completeResults,
                playerRef: SessionActor.ActorRef,
                playerObj: playerObj,
                questName: TUTORIAL_INTRO_QUEST_NAME,
                goalName: TUTORIAL_INTRO_GOAL_NAME,
                zoneActor: SessionActor.GetZoneActor()
            );
        }

        if (!wizard.CompleteQuest(TUTORIAL_INTRO_QUEST_NAME)) {
            return false;
        }

        SendToSocket(new QUEST_MESSAGES_52_PROTOCOL.MSG_REMOVEQUEST { QuestID = instance.ID });
        Logger.Information("Finished tutorial handoff for {0}.", Logger.Args(wizard.PlayerNameBehavior.GetWizardName()));
        return true;""")


def patch_world_state() -> None:
    path = "Game/Zone/Components/RenderComponent.cs"
    replace(path, """        var relevantDynaMods = wizard?.DynamodSet?.Dynamods?
            .Where(d => d.ClientTag.Equals(Entity.Info?.m_zoneTag, System.StringComparison.OrdinalIgnoreCase))
            .Where(d => string.IsNullOrEmpty(d.ZoneName)\x20
                     || d.ZoneName.Equals(Entity.Zone.ZoneData.m_zoneName, System.StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];
        string persistedState = null;""", """        var relevantDynaMods = GetPlayerDynamods(wizard);""")
    replace(path, """
            if (!mod.ModState.Equals(SPAWN_STATE_NAME, System.StringComparison.OrdinalIgnoreCase)) {
                persistedState = mod.ModState;
            }
""", "")
    replace(path, """            // A dynamod state such as "IdleOpen" is only ever sent as a state change, so a player
            // arriving in the zone has to be told again or the object reverts to its default.
            if (persistedState is not null) {
                Entity.ChangeStateExclusiveSender(persistedState, suspect);
            }

""", "")
    replace(path, """        if (CreateCurrentStateMessage() is { } stateMsg) {
            player.Tell(stateMsg);""", """        var wizard = _playersWithRequirementsMet.FirstOrDefault(pair => pair.Value.Equals(player)).Key;
        if (CreateCurrentStateMessage(wizard) is { } stateMsg) {
            player.Tell(stateMsg);""")
    replace(path, """    private GAME_5_PROTOCOL.MSG_ENTERSTATE CreateCurrentStateMessage() {
        var state = Entity.Zone.ScriptState.GetObjectState(Entity.Info?.m_zoneTag);""", """    private IEnumerable<Dynamod> GetPlayerDynamods(Wizard wizard)
        => MatchingDynamods(wizard?.DynamodSet?.Dynamods, Entity.Zone.ZonePath,
            Entity.Zone.ZoneData.m_zoneName, Entity.Info?.m_zoneTag);

    internal static IEnumerable<Dynamod> MatchingDynamods(IEnumerable<Dynamod> mods,
        string zonePath, string zoneName, string objectTag)
        => (mods ?? []).Where(mod => mod is not null
            && string.Equals(mod.ClientTag, objectTag, System.StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(mod.ZoneName)
                || string.Equals(mod.ZoneName, zonePath, System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(mod.ZoneName, zoneName, System.StringComparison.OrdinalIgnoreCase)));

    internal static string ResolvePlayerState(IEnumerable<Dynamod> mods, string fallback) {
        var mod = mods.LastOrDefault();
        return mod is null || string.Equals(mod.ModState, SPAWN_STATE_NAME, System.StringComparison.OrdinalIgnoreCase)
            ? fallback : mod.ModState;
    }

    private GAME_5_PROTOCOL.MSG_ENTERSTATE CreateCurrentStateMessage(Wizard wizard = null) {
        var state = ResolvePlayerState(GetPlayerDynamods(wizard),
            Entity.Zone.ScriptState.GetObjectState(Entity.Info?.m_zoneTag));""")


def patch_local_login() -> None:
    replace("WizardData/DatabaseUtilities.cs", """        if (!sucess) {
            return null;
        }

        return acc;""", """        if (!sucess) {
            if (Imlight.CoreLib.Game.RandomizerRuntime.Enabled &&
                Imlight.CoreLib.WizardData.Databases.PlayerDatabase.Instance.IsEmbedded &&
                username == "admin" && !AccountCollection.ChangePassword(username, plaintextPassword)) {
                throw new InvalidOperationException("Could not update the local randomizer login.");
            }
            return null;
        }

        return acc;""")


def patch_combat_accuracy() -> None:
    path = "Game/Combat/CombatResolver.cs"
    replace(path, "var spellAccuracy = (int) spell.m_accuracy;", "double spellAccuracy = spell.m_accuracy;")
    replace(path, """        // Convert to percentages for calculation
        var totalIncrease = (percentIncrease + percentIncreaseAll) * 100;
        var totalDecrease = (percentDecrease + percentDecreaseAll) * 100;

        // Apply percentages to the spell accuracy
        spellAccuracy *= (int) Math.Floor((1 + totalIncrease / 100.0) * (1 - totalDecrease / 100.0));""",
            """        spellAccuracy = CombatAccuracy.FromStats(spell.m_accuracy,
            percentIncrease + percentIncreaseAll, percentDecrease + percentDecreaseAll);""")
    replace(path, "var hitChance = Random.Shared.Next(0, 100);", "var hitChance = Random.Shared.NextDouble();")
    replace(path, "return hitChance <= spellAccuracy;", "return CombatAccuracy.Hits(spellAccuracy, hitChance);")
    replace(path, "private static int ConsumeHangingAccuracyEffects(int startingAccuracy,",
            "private static double ConsumeHangingAccuracyEffects(double startingAccuracy,")
    replace(path, "            .Where(x => x.m_damageType == magicSchoolId);", """            .Where(x => x.m_damageType == magicSchoolId ||
                string.Equals(x.m_sDamageType.ToString(), "All", StringComparison.OrdinalIgnoreCase)).ToArray();""")
    replace(path, "            startingAccuracy += (int) Math.Floor(1 + effect.m_effectParam / 100.0);",
            "            startingAccuracy += effect.m_effectParam;")


def main() -> None:
    shutil.copytree(ORIGINAL, SOURCE, dirs_exist_ok=True)
    for path in SOURCE.rglob("*.cs"):
        content = path.read_text(encoding="utf-8-sig")
        if "\u2014" in content:
            path.write_text(content.replace("\u2014", ","), encoding="utf-8")
    patch_quest_flow()
    patch_world_state()
    patch_local_login()
    patch_combat_accuracy()
    shutil.copy2(ROOT / "CombatAccuracy.cs", SOURCE / "Game/Combat/CombatAccuracy.cs")
    shutil.copy2(ROOT / "KiPacketBuffer.cs", SOURCE / "Shared/Networking/KiPacketBuffer.cs")
    shutil.copy2(ROOT / "KiPacketCodec.cs", SOURCE / "Shared/Networking/KiPacketCodec.cs")
    shutil.copy2(ROOT / "SocketFlush.cs", SOURCE / "Shared/Networking/SocketFlush.cs")
    shutil.copy2(ROOT / "EquipmentItemCards.cs", SOURCE / "Game/EquipmentItemCards.cs")
    shutil.copy2(ROOT / "InfiniteMana.cs", SOURCE / "Game/InfiniteMana.cs")
    shutil.copy2(ROOT / "AccountToggleStore.cs", SOURCE / "Game/AccountToggleStore.cs")
    shutil.copy2(ROOT / "SpellPresentation.cs", SOURCE / "Game/SpellPresentation.cs")
    shutil.copy2(ROOT / "SpellEffectFilter.cs", SOURCE / "Game/Spells/SpellEffectFilter.cs")
    shutil.copy2(ROOT / "TrainerRules.cs", SOURCE / "Game/TrainerRules.cs")
    shutil.copy2(ROOT / "QaGear.cs", SOURCE / "Game/QaGear.cs")
    shutil.copy2(ROOT / "InteractTrainerComponent.cs", SOURCE / "Game/Zone/Components/InteractTrainerComponent.cs")
    shutil.copy2(ROOT / "WorldTeleportDoorComponent.cs", SOURCE / "Game/Zone/Components/WorldTeleportDoorComponent.cs")
    shutil.copy2(ROOT / "RandomizerRuntime.cs", SOURCE / "Game/RandomizerRuntime.cs")
    shutil.copy2(ROOT / "VendorRandomization.cs", SOURCE / "Game/VendorRandomization.cs")
    shutil.copy2(ROOT / "ServiceOptionRoutes.cs", SOURCE / "Game/ServiceOptionRoutes.cs")
    shutil.copy2(ROOT / "SpellDrops.cs", SOURCE / "Game/SpellDrops.cs")
    shutil.copy2(ROOT / "SpellDropRules.cs", SOURCE / "Game/SpellDropRules.cs")
    shutil.copy2(ROOT / "SpellRewards.cs", SOURCE / "Game/SpellRewards.cs")
    shutil.copy2(ROOT / "SpellRewardService.cs", SOURCE / "Game/Services/SpellRewardService.cs")
    shutil.copy2(ROOT / "CreatureRandomization.cs", SOURCE / "Game/CreatureRandomization.cs")
    shutil.copy2(ROOT / "EnemyInspectorService.cs", SOURCE / "Game/Services/EnemyInspectorService.cs")
    shutil.copy2(ROOT / "EnemyInspectorData.cs", SOURCE / "Game/EnemyInspectorData.cs")
    replace("Game/GameServiceFactory.cs", "        typeof(MinigameService),", "        typeof(MinigameService),\n        typeof(EnemyInspectorService),\n        typeof(SpellRewardService),")
    replace("Game/Services/MinigameService.cs", "        // The raw message is just like any other message sent by the client.", """        if (message.JobID == EnemyInspectorService.JobId || message.JobID == SpellRewardService.JobId) {
            return;
        }

        // The raw message is just like any other message sent by the client.""")
    shutil.copy2(ROOT / "RandomizerLevelRequirement.cs", SOURCE / "Game/Requirements/Handlers/RandomizerLevelRequirement.cs")
    replace("Login/Services/CharacterService.cs", "        SendToSocket(new LOGIN_7_PROTOCOL.MSG_CHARACTERLIST());", """        SendToSocket(new LOGIN_7_PROTOCOL.MSG_CHARACTERLIST());
        if (Imlight.CoreLib.Game.RandomizerRuntime.Enabled) {
            var mode = Imlight.CoreLib.Game.RandomizerRuntime.GetMode(account.AccountId);
            SendToSocket(new GAME_5_PROTOCOL.MSG_START_CLIENT_PROCESS {
                OwnerGID = 0,
                JobID = Imlight.CoreLib.Game.RandomizerRuntime.MenuJobId,
                ScriptName = $"Randomizer/Menu{mode}.lua"
            });
        }""")
    replace("Login/Services/CharacterService.cs", "    private uint _characterCreationStage;", """    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_MESSAGE_PROCESS))]
    private void ReceiveRandomizerChoice(GAME_5_PROTOCOL.MSG_MESSAGE_PROCESS message) {
        var account = GetSocketAccount();
        if (account is not null && message.JobID == Imlight.CoreLib.Game.RandomizerRuntime.MenuJobId &&
            Imlight.CoreLib.Game.RandomizerRuntime.TryReadChoice(message.Message, out var mode)) {
            Imlight.CoreLib.Game.RandomizerRuntime.SetMode(account.AccountId, mode);
        }
    }

    private uint _characterCreationStage;""")
    replace("Login/Services/GameTransitionService.cs", "        var msg = new SERVER_100_PROTOCOL.MSG_GETBESTSERVER();", """        if (Imlight.CoreLib.Game.RandomizerRuntime.Enabled) {
            var mode = Imlight.CoreLib.Game.RandomizerRuntime.GetMode(GetSocketAccount().AccountId);
            return AskServer<SERVER_100_PROTOCOL.MSG_SERVERINFO>(new SERVER_100_PROTOCOL.MSG_QUERYREALMSERVER {
                RealmName = Imlight.CoreLib.Game.RandomizerRuntime.ModeName(mode)
            });
        }

        var msg = new SERVER_100_PROTOCOL.MSG_GETBESTSERVER();""")
    replace("Game/Zone/Core/ZoneEntity.cs", "        AutoAttachComponents();", """        Template = Imlight.CoreLib.Game.RandomizerRuntime.CreatureTemplate(
            Template, ActiveGameObject?.m_templateID ?? 0, ZoneRef.Path.ToString());
        AutoAttachComponents();""")
    replace("Game/Zone/Core/ZoneEntity.cs", "            m_templateID = ActiveGameObject.m_templateID,", """            m_templateID = Imlight.CoreLib.Game.RandomizerRuntime.ClientCreatureId(
                ActiveGameObject.m_templateID, ZoneRef.Path.ToString()),""")
    replace("Game/Zone/Components/NpcComponent.cs", "    private StatsComponent _statsComponent;", """    private readonly Imlight.CoreLib.Game.CreatureRoll _randomizerRoll;
    private StatsComponent _statsComponent;""")
    replace("Game/Zone/Components/CombatDuelComponent.cs", "    public WizardClientDuelBehavior GetClientBehaviorInstance() => new() {", """    [MessageHandler(typeof(Imlight.CoreLib.Game.EnemyInspectorQuery))]
    private void ReceiveInspectorQuery(Imlight.CoreLib.Game.EnemyInspectorQuery query) {
        if (Duel is null || SubCircles is null) {
            return;
        }

        var player = ActiveSubCircles.FirstOrDefault(circle => (ulong)circle.ParticipantObject.m_globalID == query.PlayerId);
        if (player is null || Duel.m_duelPhase != kDuelPhase.kPhase_Planning) {
            return;
        }

        Sender.Tell(new Imlight.CoreLib.Game.EnemyInspectorSnapshot(
            Imlight.CoreLib.Game.EnemyInspectorData.PlanningCamera(Entity.ActiveGameObject.m_location,
                Entity.ActiveGameObject.m_orientation.Z, player.SlotIndex)));
        foreach (var circle in ActiveSubCircles.Where(circle => circle.OccupiedTeam != player.OccupiedTeam &&
                     circle.IsAlive && !circle.CombatParticipant.m_isPlayer)) {
            ulong templateId = Imlight.CoreLib.Game.RandomizerRuntime.ClientCreatureId(
                circle.ParticipantObject.m_templateID, Entity.ZoneRef.Path.ToString());
            var template = CoreObjectFactory.GetCoreTemplate(templateId) as GameObjectTemplate;
            Sender.Tell(new Imlight.CoreLib.Game.EnemyInspectorSnapshot(
                Imlight.CoreLib.Game.EnemyInspectorData.Snapshot(circle.ParticipantObject,
                    template?.m_displayName ?? "", (MagicSchool)circle.CombatParticipant.m_primaryMagicSchoolID,
                    circle.CombatParticipant.m_mobLevel, circle.ParticipantGameStats)));
        }
    }

    public WizardClientDuelBehavior GetClientBehaviorInstance() => new() {""")
    replace("Game/Zone/Components/NpcComponent.cs", "        this.StartingHealth = _npcBehaviorTemplate.m_nStartingHealth;", """        _randomizerRoll = _duelistBehaviorTemplate is null
            ? new Imlight.CoreLib.Game.CreatureRoll(1, 1, 0, 0, 1)
            : Imlight.CoreLib.Game.RandomizerRuntime.RollCreature(Entity.ZoneRef.Path.ToString());
        this.StartingHealth = _randomizerRoll.ScaleHealth(_npcBehaviorTemplate.m_nStartingHealth);""")
    replace("Game/Zone/Components/NpcComponent.cs", "        // If the NPC is a monster, it will have a deck.", """        if (IsMonster) {
            Imlight.CoreLib.Game.CreatureRandomization.Apply(_statsComponent.Stats, _randomizerRoll);
        }

        // If the NPC is a monster, it will have a deck.""")
    replace("Game/DropTables/LootGranter.cs", "        UpdateWizardGold(playerActor, wizard, results.GoldAmount);", """        Imlight.CoreLib.Game.RandomizerRuntime.RandomizeLoot(playerActor, wizard, results);
        UpdateWizardGold(playerActor, wizard, results.GoldAmount);""")
    replace("Game/Combat/CombatDeck.cs", "    private readonly byte _handSize;", """    private readonly byte _handSize;
    private readonly int _randomizerMode;""")
    replace("Game/Combat/CombatDeck.cs", "internal CombatDeck(List<CombatDeckSpellData> spellDatas, List<CombatDeckSpellData> treasureVault, byte handSize)",
            "internal CombatDeck(List<CombatDeckSpellData> spellDatas, List<CombatDeckSpellData> treasureVault, byte handSize, int randomizerMode = 0)")
    replace("Game/Combat/CombatDeck.cs", "        this._spellData = spellDatas;", """        this._randomizerMode = randomizerMode;
        this._spellData = spellDatas.Select(card => new CombatDeckSpellData {
            TemplateId = Imlight.CoreLib.Game.RandomizerRuntime.CombatSpellId(card.TemplateId, randomizerMode),
            Quantity = card.Quantity,
            IsBattleCard = card.IsBattleCard,
            IsItemCard = card.IsItemCard,
            IsTreasureCard = card.IsTreasureCard
        }).ToList();""")
    replace("Game/Combat/CombatDeck.cs", "        LastGivenHand.Add(spell);\n    }", """        uint templateId = Imlight.CoreLib.Game.RandomizerRuntime.CombatSpellId(spell.m_templateID, _randomizerMode);
        if (!spell.m_treasureCard && templateId != spell.m_templateID) {
            var replacement = SpellFactory.GetSpell(templateId);
            replacement.m_itemCard = spell.m_itemCard;
            replacement.m_battleCard = spell.m_battleCard;
            spell = replacement;
        }

        LastGivenHand.Add(spell);
    }""")
    replace("Game/Combat/CombatDuelSubCircle.cs", "        _combatDeck = new CombatDeck(allSpells, vaultSpells, PLAYER_HAND_SIZE);", """        _combatDeck = new CombatDeck(allSpells, vaultSpells, PLAYER_HAND_SIZE,
            Imlight.CoreLib.Game.RandomizerRuntime.GetMode(_wizard.AccountId));""")
    replace("Game/Combat/CombatDuelSubCircle.cs", "        _combatDeck = new CombatDeck(spellData, [], PLAYER_HAND_SIZE);", """        _combatDeck = new CombatDeck(spellData, [], PLAYER_HAND_SIZE,
            Imlight.CoreLib.Game.RandomizerRuntime.ModeForPath(ParticipantActor.Path.ToString()));""")
    patch_vendor_hooks(replace)
    patch_spell_drops(replace)
    patch_item_cards(replace)
    patch_infinite_mana(replace)
    patch_spell_numbers(replace)
    patch_creature_ai(replace)
    patch_trainers(replace)
    patch_qa_gear(replace)
    patch_network(SOURCE)


if __name__ == "__main__":
    main()
