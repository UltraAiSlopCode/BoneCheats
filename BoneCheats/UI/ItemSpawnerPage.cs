using System;
using System.Collections.Generic;
using BoneCheats.Core;
using BoneCheats.Features;
using BoneLib.BoneMenu;
using UnityEngine;

namespace BoneCheats.UI
{
    /// <summary>
    /// BoneMenu > Cheats > Item Spawner > Guns | NPCs. Both sub-tabs are built from the same generic,
    /// searchable/paged list (SpawnList) so their layout is identical: a search field at the top, up to
    /// 5 spawn buttons, then Prev/Next arrows underneath. Entries and barcodes come from AssetCatalog -
    /// the game's own installed assets, never a placeholder.
    /// </summary>
    internal static class ItemSpawnerPage
    {
        public static Page Build(Page root)
        {
            Page p = root.CreatePage("Item Spawner", Color.yellow);
            AssetCatalog.EnsureBuilt();

            Lists.Clear();
            var guns = new SpawnList("Guns", Color.red, () => AssetCatalog.Guns);
            var npcs = new SpawnList("NPCs", Color.green, () => AssetCatalog.Npcs);
            Lists.Add(guns); Lists.Add(npcs);
            guns.Build(p);
            npcs.Build(p);
            return p;
        }

        private static readonly List<SpawnList> Lists = new List<SpawnList>();

        /// <summary>Called every frame: once the warehouse is ready and the catalog finally has entries, redraw the lists.</summary>
        public static void Tick()
        {
            try { if (AssetCatalog.PollRefresh()) RebuildAll(); }
            catch (Exception e) { R.WarnOnce("itemspawner-poll", "[ItemSpawner] refresh failed: " + e.Message); }
        }

        private static void RebuildAll() { foreach (SpawnList l in Lists) l.Redraw(); }

        /// <summary>One searchable, paged button list. Guns and NPCs each get their own instance so search
        /// text and the current page never leak between the two tabs, even though the code path is identical.</summary>
        private sealed class SpawnList
        {
            private const int PerPage = 5;

            private readonly string _title;
            private readonly Color _color;
            private readonly Func<List<SpawnEntry>> _source;
            private Page _page;
            private string _search = "";
            private int _pageIndex;

            public SpawnList(string title, Color color, Func<List<SpawnEntry>> source)
            {
                _title = title;
                _color = color;
                _source = source;
            }

            public void Build(Page root)
            {
                _page = root.CreatePage(_title, _color);
                Rebuild();
            }

            public void Redraw() { if (_page != null) Rebuild(); }

            private List<SpawnEntry> Filtered()
            {
                List<SpawnEntry> all = _source();
                if (string.IsNullOrEmpty(_search)) return all;

                var result = new List<SpawnEntry>();
                foreach (SpawnEntry e in all)
                    if (e.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0) result.Add(e);
                return result;
            }

            /// <summary>Clears and refills the page: search field, then up to 5 buttons for the current page, then Prev/Next.</summary>
            private void Rebuild()
            {
                // Re-check in case the first catalog read ran before the warehouse was populated.
                AssetCatalog.RebuildIfEmpty();

                List<SpawnEntry> list = Filtered();
                int pageCount = Math.Max(1, (list.Count + PerPage - 1) / PerPage);
                if (_pageIndex >= pageCount) _pageIndex = pageCount - 1;
                if (_pageIndex < 0) _pageIndex = 0;

                _page.RemoveAll();

                // Recreated every rebuild (search text and page nav all go through this method) - its initial
                // value is seeded from _search so switching pages never clears what was typed.
                MenuBuilder.TryCreateTextField(_page, "Search: (point here to type)", Color.white, _search, typed =>
                {
                    _search = typed ?? "";
                    _pageIndex = 0; // a new search always starts back on page 1
                    Rebuild();
                });

                _page.CreateFunction("Refresh List", Color.yellow, () =>
                {
                    AssetCatalog.ForceRebuild();
                    ItemSpawnerPage.RebuildAll();
                });

                if (list.Count == 0)
                {
                    _page.CreateFunction(string.IsNullOrEmpty(_search) ? "(none found - load a level, then Refresh List)" : "(no matches)", Color.gray, () => { });
                }
                else
                {
                    int start = _pageIndex * PerPage;
                    int end = Math.Min(start + PerPage, list.Count);
                    for (int i = start; i < end; i++)
                    {
                        SpawnEntry entry = list[i]; // local copy - this is what the closure below must capture
                        _page.CreateFunction(entry.Name, Color.white, () => Spawner.SpawnBarcode(entry.Barcode));
                    }
                }

                _page.CreateFunction("<< Prev", Color.cyan, () =>
                {
                    if (_pageIndex <= 0) return;
                    _pageIndex--;
                    Rebuild();
                });
                _page.CreateFunction("Page " + (_pageIndex + 1) + " / " + pageCount, Color.gray, () => { });
                _page.CreateFunction("Next >>", Color.cyan, () =>
                {
                    if (_pageIndex >= pageCount - 1) return;
                    _pageIndex++;
                    Rebuild();
                });
            }
        }
    }
}
