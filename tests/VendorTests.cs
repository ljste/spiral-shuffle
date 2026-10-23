using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imcodec.Wad;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;
using Type = System.Type;

internal static class VendorTests {
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    internal static int Run(Assembly assembly, string root) {
        Type storeType = assembly.GetType("Imlight.CoreLib.Game.VendorSettingsStore")!;
        Type stockType = assembly.GetType("Imlight.CoreLib.Game.VendorStock")!;
        string file = Path.Combine(Directory.GetCurrentDirectory(), "vendor-tests", Guid.NewGuid().ToString(), "state.json");
        object store = Activator.CreateInstance(storeType, Instance, null, [file], null)!;
        MethodInfo seed = storeType.GetMethod("SeedForWizard", Instance)!;
        MethodInfo enabled = storeType.GetMethod("IsEnabled", Instance)!;
        MethodInfo set = storeType.GetMethod("SetEnabled", Instance)!;
        MethodInfo choose = stockType.GetMethod("Choose", Static)!.MakeGenericMethod(typeof(int));
        int[] pool = Enumerable.Range(1, 500).ToArray();
        Func<int, string> identity = value => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        HashSet<string> seeds = [];
        HashSet<string> inventories = [];
        int checks = 0;

        Require(!(bool)enabled.Invoke(store, [1UL])!, "Vendor shuffle must default to off.");
        set.Invoke(store, [1UL, true]);
        for (ulong wizard = 1; wizard <= 128; wizard++) {
            byte[] key = (byte[])seed.Invoke(store, [wizard])!;
            Require(key.Length == 32 && seeds.Add(Convert.ToHexString(key)), "Wizards shared an entropy seed.");
            int[] roll = (int[])choose.Invoke(null, [pool, identity, key, 101UL, "items", 12])!;
            int[] reopened = (int[])choose.Invoke(null, [pool.Reverse(), identity, key, 101UL, "items", 12])!;
            Require(roll.SequenceEqual(reopened), "Reopening or catalog ordering changed the stock.");
            Require(roll.Length == 12 && roll.Distinct().Count() == 12, "Duplicate or missing shop slots.");
            Require(inventories.Add(string.Join(',', roll)), "Two new wizards received identical stock.");
            checks += 4;
        }

        object reloaded = Activator.CreateInstance(storeType, Instance, null, [file], null)!;
        Require((bool)enabled.Invoke(reloaded, [1UL])!, "Vendor preference did not survive a reload.");
        byte[] saved = (byte[])seed.Invoke(store, [1UL])!;
        Require(saved.SequenceEqual((byte[])seed.Invoke(reloaded, [1UL])!), "A reload rerolled the wizard seed.");
        int[] first = (int[])choose.Invoke(null, [pool, identity, saved, 101UL, "items", 12])!;
        int[] otherVendor = (int[])choose.Invoke(null, [pool, identity, saved, 102UL, "items", 12])!;
        Require(!first.SequenceEqual(otherVendor), "Two vendors shared the same stock.");
        Task<byte[]>[] concurrent = Enumerable.Range(1, 16).Select(_ => Task.Run(() =>
            (byte[])seed.Invoke(store, [999UL])!)).ToArray();
        Task.WaitAll(concurrent);
        Require(concurrent.All(task => task.Result.SequenceEqual(concurrent[0].Result)), "Concurrent visits rerolled stock.");
        checks += 5;

        Type runtime = assembly.GetType("Imlight.CoreLib.Game.RandomizerRuntime")!;
        MethodInfo options = runtime.GetMethod("TryReadOptions", Static)!;
        foreach (bool vendors in new[] { false, true }) {
            for (int mode = 1; mode <= 6; mode++) {
                byte[] packet = MessageEncoder.Encode(new MG2_MESSAGES_43_PROTOCOL.MSG_MG2_REWARDS {
                    score = mode + (vendors ? 16 : 0), gameName = "SpiralShuffle"
                });
                object?[] arguments = [(ByteString)packet[8..], 0, false];
                Require((bool)options.Invoke(null, arguments)! && (int)arguments[1]! == mode &&
                    (bool)arguments[2]! == vendors, "Menu options did not round trip.");
                checks++;
            }
        }

        checks += VerifyShops(assembly, root);
        Console.WriteLine($"Passed {checks} vendor checks: fresh wizard seeds, saved rolls, twelve menu choices, shop isolation and purchase validation.");
        return checks;
    }

    private static int VerifyShops(Assembly assembly, string root) {
        using FileStream stream = File.OpenRead(Path.Combine(root, ".runtime/patch/V_r806919.Wizard_1_610/Data/GameData/Root.wad"));
        Archive archive = ArchiveParser.Parse(stream)!;
        Type loader = assembly.GetType("Imlight.CoreLib.Shared.Resources.RootArchiveLoader")!;
        loader.GetField("s_rootWad", Static)!.SetValue(null, archive);
        _ = typeof(CoreObjectFactory).GetProperty("Instance", Static | BindingFlags.FlattenHierarchy)!.GetValue(null);
        Type spells = assembly.GetType("Imlight.CoreLib.Game.Spells.SpellFactory")!;
        _ = spells.GetProperty("Instance", Static | BindingFlags.FlattenHierarchy)!.GetValue(null);
        SpiralDB.Load();
        Type vendors = assembly.GetType("Imlight.CoreLib.Game.VendorRandomization")!;
        MethodInfo set = vendors.GetMethod("SetEnabled", Static)!;
        MethodInfo items = vendors.GetMethod("Items", Static)!;
        MethodInfo cards = vendors.GetMethod("Cards", Static)!;
        Wizard first = new() { AccountId = 900001, CharId = 910001 };
        Wizard second = new() { AccountId = 900001, CharId = 910002 };
        set.Invoke(null, [first.AccountId, true]);
        int count = 0;
        foreach (NPCInventory shop in SpiralDB.NpcInventories.Values) {
            GID[] original = shop.Inventory.ToArray();
            List<GID> a = (List<GID>)items.Invoke(null, [first, shop.TemplateID, shop.Inventory])!;
            List<GID> b = (List<GID>)items.Invoke(null, [second, shop.TemplateID, shop.Inventory])!;
            Require(a.Count is >= 8 and <= 48, "An item shop has no randomized stock.");
            Require(!a.SequenceEqual(b), "An item shop reused another wizard's stock.");
            Require(original.SequenceEqual(shop.Inventory), "A shared NPC inventory was mutated.");
            object component = Component(assembly, "InteractVendorComponent", shop.TemplateID);
            MethodInfo has = component.GetType().GetMethod("HasItem")!;
            GID exclusive = a.First(item => !b.Contains(item));
            Require((bool)has.Invoke(component, [exclusive, first])! &&
                !(bool)has.Invoke(component, [exclusive, second])!, "Item purchase validation crossed wizard inventories.");
            count += 4;
        }

        foreach (NpcTreasureCardInventory shop in SpiralDB.TreasureCardInventories.Values) {
            List<TreasureCardEntry> a = (List<TreasureCardEntry>)cards.Invoke(null, [first, shop.TemplateID, shop.TreasureCards])!;
            List<TreasureCardEntry> b = (List<TreasureCardEntry>)cards.Invoke(null, [second, shop.TemplateID, shop.TreasureCards])!;
            Require(a.Count > 0 && !a.Select(card => card.SpellName).SequenceEqual(b.Select(card => card.SpellName)),
                "Treasure card stock was not randomized per wizard.");
            HashSet<string> ordinary = SpiralDB.TreasureCardInventories.Values
                .SelectMany(vendor => vendor.TreasureCards).Select(card => card.SpellName).ToHashSet();
            Require(a.Count(card => !ordinary.Contains(card.SpellName)) > a.Count / 2,
                "Treasure stock still comes mainly from the ordinary vendor catalog.");
            MethodInfo getSpell = spells.GetMethod("GetSpell", Static, [typeof(string)])!;
            foreach (TreasureCardEntry card in a) {
                Spell spell = (Spell)getSpell.Invoke(null, [card.SpellName])!;
                SpellTemplate template = (SpellTemplate)CoreObjectFactory.GetCoreTemplate(spell.m_templateID);
                Require(template.m_Treasure && template.m_baseCost == card.Price,
                    "A rolled treasure card is invalid or its purchase price differs from its display price.");
                count++;
            }
            object component = Component(assembly, "InteractTreasureVendorComponent", shop.TemplateID);
            TreasureCardEntry exclusive = a.First(card => b.All(other => other.SpellName != card.SpellName));
            uint hash = StringHash.Compute(exclusive.SpellName);
            MethodInfo has = component.GetType().GetMethod("HasSpell", [typeof(uint), typeof(Wizard)])!;
            Require((bool)has.Invoke(component, [hash, first])! && !(bool)has.Invoke(component, [hash, second])!,
                "Treasure purchase validation crossed wizard inventories.");
            MethodInfo price = component.GetType().GetMethod("GetSpellPrice", [typeof(uint), typeof(Wizard)])!;
            Require((int)price.Invoke(component, [hash, first])! == exclusive.Price, "Treasure price changed at purchase.");
            count += 4;
        }

        object cardPool = vendors.GetField("CardPool", Static)!.GetValue(null)!;
        TreasureCardEntry[] catalog = (TreasureCardEntry[])cardPool.GetType().GetProperty("Value")!.GetValue(cardPool)!;
        Require(catalog.Length > 1000, "The treasure catalog did not include the full game's card pool.");
        Console.WriteLine($"Treasure vendor pool: {catalog.Length} registered treasure cards.");
        count++;

        set.Invoke(null, [first.AccountId, false]);
        NPCInventory vanilla = SpiralDB.NpcInventories.Values.First(shop => shop.Inventory.Count > 0);
        MethodInfo isQa = assembly.GetType("Imlight.CoreLib.Game.QaGear")!.GetMethod("IsQa", Static)!;
        GID[] ordinaryStock = vanilla.Inventory.Where(item => !(bool)isQa.Invoke(null, [(ulong)item.MParts.TemplateId])!).ToArray();
        Require(((List<GID>)items.Invoke(null, [first, vanilla.TemplateID, vanilla.Inventory])!).SequenceEqual(ordinaryStock),
            "Turning vendor shuffle off did not restore ordinary stock.");
        Console.WriteLine($"Audited {SpiralDB.NpcInventories.Count} item shops and {SpiralDB.TreasureCardInventories.Count} treasure shops.");
        return count + 1;
    }

    private static object Component(Assembly assembly, string name, ulong vendorId) {
        Type entityType = assembly.GetType("Imlight.CoreLib.Game.Zone.Core.ZoneEntity")!;
        object entity = RuntimeHelpers.GetUninitializedObject(entityType);
        entityType.GetProperty("ActiveGameObject", Instance)!.SetValue(entity, new WizClientObjectItem {
            m_templateID = (GID)vendorId
        });
        Type componentType = assembly.GetType($"Imlight.CoreLib.Game.Zone.Components.{name}")!;
        object component = Activator.CreateInstance(componentType, Instance, null, [entity], null)!;
        componentType.GetMethod("OnStart")!.Invoke(component, null);
        return component;
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }
}
