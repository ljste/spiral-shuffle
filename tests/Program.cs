using System.Reflection;
using System.Collections;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;

internal static class Program {
    private static int Main(string[] args) {
        string root = Path.GetFullPath(args[0]);
        string directory = Path.Combine(root, ".build/test-work");
        Directory.CreateDirectory(Path.Combine(directory, "Config"));
        File.Copy(Path.Combine(root, ".runtime/generated/catalog.json"), Path.Combine(directory, "Config/Randomizer.json"), true);
        Directory.SetCurrentDirectory(directory);
        ConfigurationManager.Initialize(Path.Combine(root, ".runtime/server/Config/Imlight.ini"));
        System.Type runtime = Assembly.Load("Imlight.CoreLib").GetType("Imlight.CoreLib.Game.RandomizerRuntime")!;
        MethodInfo clientId = runtime.GetMethod("ClientCreatureId", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo combatId = runtime.GetMethod("CombatSpellId", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo choice = runtime.GetMethod("TryReadChoice", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo get = runtime.GetMethod("GetMode", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo set = runtime.GetMethod("SetMode", BindingFlags.Static | BindingFlags.NonPublic)!;
        int checks = 0;
        checks += NetworkTests.Run(runtime.Assembly);
        using System.Text.Json.JsonDocument catalog = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, ".runtime/generated/catalog.json")));
        System.Text.Json.JsonElement creature = catalog.RootElement.GetProperty("CreatureSwaps")[0];
        uint originalId = creature.GetProperty("From").GetUInt32();
        uint aliasId = creature.GetProperty("Alias").GetUInt32();
        System.Text.Json.JsonElement[] spellVariants = catalog.RootElement.GetProperty("SpellVariants").EnumerateArray().ToArray();
        for (int mode = 1; mode <= 6; mode++) {
            string path = $"akka://Imlight/user/Imlight.Game.{12342 + mode}/World/Zone";
            Equal(mode, CreatureRandomization.ModeForPath(path, 12343));
            string namedPath = $"akka://Imlight/user/GameServerPool/Imlight.Game.{CreatureRandomization.RealmName(mode)}/World/Zone";
            Equal(mode, CreatureRandomization.ModeForPath(namedPath, 12343));
            string actualPath = $"akka://Imlight/user/Imlight.Login/Imlight.Login.GameServerPool/Imlight.Game.{CreatureRandomization.RealmName(mode)}.{12342 + mode}/Imlight.Game.{CreatureRandomization.RealmName(mode)}.GameWorld/Zone";
            Equal(mode, CreatureRandomization.ModeForPath(actualPath, 12343));
            Equal((ulong)(mode > 3 ? aliasId : originalId), clientId.Invoke(null, [(ulong)originalId, path]));
            Equal((ulong)(mode > 3 ? aliasId : originalId), clientId.Invoke(null, [(ulong)originalId, namedPath]));
            Equal((ulong)(mode > 3 ? aliasId : originalId), clientId.Invoke(null, [(ulong)originalId, actualPath]));
            byte[] packet = MessageEncoder.Encode(new MG2_MESSAGES_43_PROTOCOL.MSG_MG2_REWARDS { score = mode, gameName = "SpiralShuffle" });
            object?[] arguments = [(ByteString)packet[8..], 0];
            Equal(true, choice.Invoke(null, arguments));
            Equal(mode, arguments[1]);
            set.Invoke(null, [999UL, mode]);
            Equal(mode, get.Invoke(null, [999UL]));
            checks += 9;
            checks += VerifyRolls(mode);
            foreach (System.Text.Json.JsonElement variant in spellVariants.Where(value =>
                value.GetProperty("Difficulty").GetInt32() == CreatureRandomization.Difficulty(mode))) {
                uint original = variant.GetProperty("Original").GetUInt32();
                Equal(variant.GetProperty("TemplateId").GetUInt32(), combatId.Invoke(null, [original, mode]));
                checks++;
            }

            System.Text.Json.JsonElement sample = spellVariants.First(value =>
                value.GetProperty("Difficulty").GetInt32() == CreatureRandomization.Difficulty(mode));
            checks += VerifyCombatDeck(runtime.Assembly, sample.GetProperty("Original").GetUInt32(),
                sample.GetProperty("TemplateId").GetUInt32(), mode);
        }

        byte[] invalid = MessageEncoder.Encode(new MG2_MESSAGES_43_PROTOCOL.MSG_MG2_REWARDS { score = 50, gameName = "SpiralShuffle" });
        Equal(false, choice.Invoke(null, [(ByteString)invalid[8..], 0]));
        checks++;
        Equal(1U, combatId.Invoke(null, [1U, 3]));
        Equal(spellVariants[0].GetProperty("Original").GetUInt32(), combatId.Invoke(null,
            [spellVariants[0].GetProperty("Original").GetUInt32(), 0]));
        checks += 2;
        RequirementList requirements = new() { m_requirements = [new ReqMagicLevel {
            m_numericValue = 5, m_operatorType = OPERATOR_TYPE.OPERATOR_GREATER_THAN_EQ
        }] };
        foreach ((int level, bool allowed) in new[] { (4, false), (5, true), (12, true) }) {
            Wizard wizard = new() { MagicSchoolBehavior = new ServerMagicSchoolBehavior { Level = level } };
            GenericRequirementContext context = new(requirements, null!, wizard.GameObject, wizard);
            Equal(allowed, RequirementDispatcher.EvaluateRequirements(requirements, context));
            checks++;
        }

        checks += VerifyEnemyInspector(runtime.Assembly);
        checks += WorldStateTests.Run(runtime.Assembly);
        checks += VendorTests.Run(runtime.Assembly, root);
        checks += NetworkTests.CheckAssetRetries(runtime.Assembly, root);
        checks += ServiceRoutingTests.Run(runtime.Assembly);
        checks += SpellDropTests.Run(runtime.Assembly, root);
        checks += SpellRewardTests.Run();
        checks += CombatAccuracyTests.Run(runtime.Assembly);
        checks += EquipmentItemCardTests.Run(runtime.Assembly, root);
        checks += InfiniteManaTests.Run(runtime.Assembly);
        checks += SpellNumberTests.Run(runtime.Assembly, root);
        checks += CreatureTurnTests.Run(runtime.Assembly);
        checks += TrainerTests.Run(runtime.Assembly, root);
        checks += QaGearTests.Run(runtime.Assembly, root);
        Console.WriteLine($"Passed {checks} runtime checks: six mode choices, creature routing, individual stat bounds, spell variants, loot requirements, and enemy inspector values.");
        return 0;
    }

    private static void Equal(object expected, object? actual) {
        if (!expected.Equals(actual)) {
            throw new InvalidDataException($"Expected {expected}, received {actual}");
        }
    }

    private static int VerifyRolls(int mode) {
        int difficulty = CreatureRandomization.Difficulty(mode);
        (double minimum, double maximum, float damageMin, float damageMax, float resistMax,
            float accuracyMax, float ratingMin, float ratingMax) = difficulty switch {
            3 => (1.125, 3.9375, 0.50f, 1.85f, 0.20f, 0.15f, 0.50f, 1.50f),
            2 => (0.91, 2.10, 0.65f, 1.50f, 0.12f, 0.10f, 0.65f, 1.35f),
            _ => (0.80, 1.20, 0.80f, 1.20f, 0.05f, 0.05f, 0.80f, 1.20f)
        };
        Random random = new(101);
        HashSet<int> healthValues = [];
        for (int index = 0; index < 10_000; index++) {
            CreatureRoll roll = CreatureRandomization.Create(mode, random);
            Require(roll.Health >= minimum && roll.Health <= maximum, "Health out of bounds.");
            Require(roll.Damage >= damageMin && roll.Damage <= damageMax, "Damage out of bounds.");
            Require(roll.Resistance >= 0 && roll.Resistance <= resistMax, "Resistance out of bounds.");
            Require(Math.Abs(roll.Accuracy) <= accuracyMax, "Accuracy out of bounds.");
            Require(roll.Rating >= ratingMin && roll.Rating <= ratingMax, "Rating out of bounds.");
            healthValues.Add(roll.ScaleHealth(100));
            ServerWizGameStats stats = new(MagicSchool.Death, 1) {
                m_dmgBonusPercentAll = 0.20f, m_dmgBonusPercent = [0.10f],
                m_dmgReducePercentAll = 0.10f, m_dmgReducePercent = [0.05f, 1f],
                m_criticalHitRatingAll = 0, m_blockRatingAll = 100,
                m_blockRatingBySchool = [50]
            };
            CreatureRandomization.Apply(stats, roll);
            Require(Math.Abs(1 + stats.m_dmgBonusPercentAll + stats.m_dmgBonusPercent[0] - 1.30f * roll.Damage) < 0.00001,
                "School and universal damage modifiers disagree.");
            Require(stats.m_dmgReducePercentAll + stats.m_dmgReducePercent[1] <= 0.950001f,
                "Resistance exceeded its cap.");
            Equal(0f, stats.m_criticalHitRatingAll);
            Equal(true, stats.m_criticalHitRatingBySchool is null);
            Equal(100 * roll.Rating, stats.m_blockRatingAll);
            Equal(50 * roll.Rating, stats.m_blockRatingBySchool[0]);
            Equal(1, roll.ScaleHealth(0));
        }

        Require(healthValues.Count > 20, "Individual creatures did not receive different health rolls.");
        ServerWizGameStats zero = new(MagicSchool.Death, 1);
        CreatureRandomization.Apply(zero, CreatureRandomization.Create(mode, random));
        Equal(true, zero.m_dmgBonusPercent is null);
        Equal(true, zero.m_dmgReducePercent is null);
        Equal(true, zero.m_criticalHitRatingBySchool is null);
        Equal(true, zero.m_blockRatingBySchool is null);
        return 120_005;
    }

    private static int VerifyCombatDeck(Assembly assembly, uint originalId, uint expectedId, int mode) {
        System.Type cardType = assembly.GetType("Imlight.CoreLib.Game.Combat.CombatDeckSpellData")!;
        System.Type listType = typeof(List<>).MakeGenericType(cardType);
        IList cards = (IList)Activator.CreateInstance(listType)!;
        object card = Activator.CreateInstance(cardType)!;
        cardType.GetProperty("TemplateId")!.SetValue(card, originalId);
        cardType.GetProperty("Quantity")!.SetValue(card, 3U);
        cardType.GetProperty("IsItemCard")!.SetValue(card, true);
        cards.Add(card);
        IList vault = (IList)Activator.CreateInstance(listType)!;
        vault.Add(card);
        System.Type deckType = assembly.GetType("Imlight.CoreLib.Game.Combat.CombatDeck")!;
        object deck = Activator.CreateInstance(deckType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [cards, vault, (byte)7, mode], null)!;
        IList combatCards = (IList)deckType.GetField("_spellData", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(deck)!;
        IList combatVault = (IList)deckType.GetField("_treasureVaultUsed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(deck)!;
        Equal(expectedId, cardType.GetProperty("TemplateId")!.GetValue(combatCards[0]));
        Equal(3U, cardType.GetProperty("Quantity")!.GetValue(combatCards[0]));
        Equal(true, cardType.GetProperty("IsItemCard")!.GetValue(combatCards[0]));
        Equal(originalId, cardType.GetProperty("TemplateId")!.GetValue(card));
        Equal(originalId, cardType.GetProperty("TemplateId")!.GetValue(combatVault[0]));
        return 5;
    }

    private static int VerifyEnemyInspector(Assembly assembly) {
        string[] schools = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Shadow"];
        System.Type magicSchools = assembly.GetType("Imlight.CoreLib.Game.Spells.MagicSchools")!;
        IDictionary schoolData = (IDictionary)magicSchools.GetField("s_magicSchools", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        schoolData.Clear();
        for (int index = 0; index < schools.Length; index++) {
            schoolData.Add(index, new MagicSchoolTemplate { m_schoolName = schools[index], m_schoolIndex = index });
        }

        System.Type effectConfig = assembly.GetType("Imlight.CoreLib.Game.Effects.WizStatisticEffectConfigLoader")!;
        effectConfig.GetField("s_config", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null,
            new WizStatisticEffectConfig { m_criticalHitLevelThreshold = 50 });
        System.Type inspector = assembly.GetType("Imlight.CoreLib.Game.EnemyInspectorData")!;
        MethodInfo snapshot = inspector.GetMethod("Snapshot", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo camera = inspector.GetMethod("PlanningCamera", BindingFlags.Static | BindingFlags.NonPublic)!;
        var creature = new CoreObject { m_globalID = 1234567890123456789UL, m_location = new Imcodec.Math.Vector3(10, 20, 30) };
        ServerWizGameStats stats = new(MagicSchool.Death, 1) {
            m_currentHitpoints = 125, m_baseHitpoints = 193,
            m_dmgBonusPercentAll = 0.10f, m_dmgBonusPercent = [0.01f, 0.02f, 0.03f, 0.04f, 0.05f, 0.06f, 0.07f, 0.08f],
            m_dmgReducePercentAll = 0.15f, m_accBonusPercentAll = -0.04f,
            m_criticalHitRatingAll = 100, m_blockRatingAll = 200
        };
        string[] fields = ((string)snapshot.Invoke(null, [creature, "WizardMobs_00000027", MagicSchool.Balance, 1, stats, 1234])!).Split('|');
        Equal(13, fields.Length);
        Equal("0.1234%", fields[12]);
        Equal("1234567890123456789", fields[1]);
        Equal("125", fields[8]);
        Equal("193", fields[9]);
        string[] fire = fields[10].Split(';')[0].Split(',');
        string[] death = fields[10].Split(';')[5].Split(',');
        Equal("Fire", fire[0]);
        Equal("11", fire[1]);
        Equal("Death", death[0]);
        Equal("16", death[1]);
        Equal("15", death[3]);
        Equal("-4", death[5]);
        Equal("100", death[7]);
        Equal("0", death[9]);
        Equal(true, stats.m_dmgReducePercent is null);
        stats.m_accReducePercentAll = 0.03f;
        stats.m_accReducePercent = [0.02f];
        fields = ((string)snapshot.Invoke(null, [creature, "name", MagicSchool.Death, 1, stats, 0])!).Split('|');
        Equal("-9", fields[10].Split(';')[0].Split(',')[5]);
        Equal("-7", fields[10].Split(';')[5].Split(',')[5]);
        fields = ((string)snapshot.Invoke(null, [creature, "name", MagicSchool.Death, 60, stats, 0])!).Split('|');
        Equal("OFF", fields[12]);
        Require(float.Parse(fields[10].Split(';')[0].Split(',')[9], System.Globalization.CultureInfo.InvariantCulture) > 0,
            "High level critical chance was omitted from the inspector.");
        Equal("Planning|10|20|30|0.12345679|1", camera.Invoke(null, [creature.m_location, 0.12345679f, 4]));
        Equal("Planning|10|20|30|0|-1", camera.Invoke(null, [creature.m_location, 0f, 0]));
        return 20;
    }

    private static void Require(bool condition, string message) {
        if (!condition) {
            throw new InvalidDataException(message);
        }
    }
}
