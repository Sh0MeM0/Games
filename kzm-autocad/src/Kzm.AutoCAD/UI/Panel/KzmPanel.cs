using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Kzm.AutoCAD.Services;
using Kzm.Core;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Kzm.AutoCAD.UI.Panel
{
    /// <summary>One detail area of the panel (below the tree).</summary>
    internal interface IPanelView
    {
        /// <summary>Re-reads what it shows for the active drawing.</summary>
        void Bind(KzmPanel panel, string surfaceId);
    }

    /// <summary>
    /// Main control of the side panel: toolbar, tree of surfaces
    /// («Фактические» / «Проектные» → поверхность → «Точки», «Близкие точки»)
    /// and the detail area for the selected node, the import form or the settings.
    /// </summary>
    internal sealed class KzmPanel : UserControl
    {
        private enum NodeKind { Group, Surface, Points, Pairs, CartogramGroup, Cartogram }

        private sealed class NodeTag
        {
            internal NodeKind Kind;
            internal string SurfaceId;
            internal SurfaceKind Group;
            internal string Key => Kind + ":" + (SurfaceId ?? Group.ToString());
        }

        private readonly TreeView tree = new TreeView();
        private readonly System.Windows.Forms.Panel detail = new System.Windows.Forms.Panel();
        private readonly Label status = new Label();
        private readonly SurfaceView surfaceView = new SurfaceView();
        private readonly PointsView pointsView = new PointsView();
        private readonly PairsView pairsView = new PairsView();
        private readonly ImportView importView = new ImportView();
        private readonly SettingsView settingsView = new SettingsView();
        private readonly CartogramView cartogramView = new CartogramView();
        private readonly ContextMenuStrip surfaceMenu = new ContextMenuStrip();
        private Control shown;
        private string shownSurfaceId;
        private string selectedKey;
        private bool rebuilding;

        internal KzmPanel()
        {
            Dock = DockStyle.Fill;
            Font = SystemFonts.MessageBoxFont;

            var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top, Stretch = true };
            toolbar.Items.Add(Button("+ Поверхность", "Создать фактическую или проектную поверхность", () => ShowNewSurface()));
            toolbar.Items.Add(Button("+ Картограмма", "Новая картограмма: граница, поверхности, сетка, оформление", ShowNewCartogram));
            toolbar.Items.Add(Button("Импорт", "Импорт отметок из текстов съёмки", () => ShowImport(SelectedSurfaceId)));
            toolbar.Items.Add(Button("Настройки", "Настройки чертежа: масштаб, размеры, шаблон числа, диапазон", ShowSettings));
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(Button("Убрать пометки", "Удалить пометки проверки со слоя КЗМ_ПРОВЕРКА", ClearMarks));
            toolbar.Items.Add(Button("Обновить", "Перечитать данные чертежа", RefreshAll));

            // Size first: WinForms rejects a SplitterDistance larger than the default 150×100 control.
            var split = new SplitContainer { Size = new Size(380, 900), Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel1 };
            split.SplitterDistance = 170;
            split.Dock = DockStyle.Fill;
            tree.Dock = DockStyle.Fill;
            tree.HideSelection = false;
            tree.ShowNodeToolTips = true;
            tree.AfterSelect += (s, e) => { if (!rebuilding) OnNodeSelected(e.Node); };
            tree.NodeMouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                tree.SelectedNode = e.Node;
                if (e.Node.Tag is NodeTag tag && tag.Kind == NodeKind.Surface) surfaceMenu.Show(tree, e.Location);
            };
            split.Panel1.Controls.Add(tree);
            detail.Dock = DockStyle.Fill;
            detail.AutoScroll = true;
            split.Panel2.Controls.Add(detail);

            status.Dock = DockStyle.Bottom;
            status.AutoSize = false;
            status.Height = 36;
            status.Padding = new Padding(4, 2, 4, 2);
            status.TextAlign = ContentAlignment.MiddleLeft;

            surfaceMenu.Items.Add("Импорт отметок в эту поверхность", null, (s, e) => ShowImport(SelectedSurfaceId));
            surfaceMenu.Items.Add("Точки", null, (s, e) => Select(NodeKind.Points, SelectedSurfaceId));
            surfaceMenu.Items.Add("Сделать текущей", null, (s, e) => SetCurrent(SelectedSurfaceId));
            surfaceMenu.Items.Add("Свойства", null, (s, e) => Select(NodeKind.Surface, SelectedSurfaceId));

            Controls.Add(split);
            Controls.Add(toolbar);
            Controls.Add(status);
            PanelMessages.Posted += (text, error) =>
            {
                status.ForeColor = error ? Color.Firebrick : SystemColors.ControlText;
                status.Text = text;
            };
        }

        internal Document Document { get; private set; }
        internal DrawingSnapshot Snapshot { get; private set; } = new DrawingSnapshot();

        internal string SelectedSurfaceId => (tree.SelectedNode?.Tag as NodeTag)?.SurfaceId ?? Snapshot.CurrentId;

        private static ToolStripButton Button(string text, string tip, Action action)
        {
            var button = new ToolStripButton(text) { ToolTipText = tip, DisplayStyle = ToolStripItemDisplayStyle.Text };
            button.Click += (s, e) => action();
            return button;
        }

        /// <summary>Re-reads the drawing and redraws the tree and the open view.</summary>
        internal void RefreshAll()
        {
            Document = AcadApplication.DocumentManager.MdiActiveDocument;
            Snapshot = DrawingData.Read(Document);
            if (Snapshot.Error != null) PanelMessages.Error("данные КЗМ в чертеже не читаются: " + Snapshot.Error);
            RebuildTree();
            if (shown is IPanelView view) view.Bind(this, shownSurfaceId);
            if (Document == null) PanelMessages.Info("Нет открытого чертежа.");
        }

        internal void Forget(Document document) => importView.Forget(document);

        /// <summary>The import form takes the new drawing settings.</summary>
        internal void SettingsSaved() => importView.ResetSettings(Document);

        private void RebuildTree()
        {
            rebuilding = true;
            tree.BeginUpdate();
            try
            {
                tree.Nodes.Clear();
                TreeNode toSelect = null;
                foreach (var kind in new[] { SurfaceKind.Fact, SurfaceKind.Project })
                {
                    var surfaces = Snapshot.Surfaces.Where(surface => surface.Info.Kind == kind).ToList();
                    var group = new TreeNode((kind == SurfaceKind.Fact ? "Фактические" : "Проектные") + " (" + surfaces.Count + ")")
                    {
                        Tag = new NodeTag { Kind = NodeKind.Group, Group = kind },
                    };
                    foreach (var surface in surfaces)
                    {
                        var node = new TreeNode(surface.Info.Name + "  —  " + surface.Points + " т." + (surface.IsCurrent ? "  (текущая)" : string.Empty))
                        {
                            Tag = new NodeTag { Kind = NodeKind.Surface, SurfaceId = surface.Info.Id },
                            ToolTipText = "Слой " + surface.Info.Layer,
                        };
                        node.Nodes.Add(new TreeNode("Точки (" + surface.Points + (surface.BrokenPoints > 0 ? ", с ошибкой " + surface.BrokenPoints : string.Empty) + ")")
                        {
                            Tag = new NodeTag { Kind = NodeKind.Points, SurfaceId = surface.Info.Id },
                            ForeColor = surface.BrokenPoints > 0 ? Color.Firebrick : tree.ForeColor,
                        });
                        if (surface.PendingPairs > 0)
                            node.Nodes.Add(new TreeNode("Близкие точки (" + surface.PendingPairs + ")")
                            {
                                Tag = new NodeTag { Kind = NodeKind.Pairs, SurfaceId = surface.Info.Id },
                                ForeColor = Color.Firebrick,
                            });
                        group.Nodes.Add(node);
                        node.Expand();
                        foreach (TreeNode child in node.Nodes)
                            if (((NodeTag)child.Tag).Key == selectedKey) toSelect = child;
                        if (((NodeTag)node.Tag).Key == selectedKey) toSelect = node;
                    }
                    tree.Nodes.Add(group);
                    group.Expand();
                    if (((NodeTag)group.Tag).Key == selectedKey) toSelect = group;
                }
                var cartograms = new TreeNode("Картограммы (" + Snapshot.Cartograms.Count + ")") { Tag = new NodeTag { Kind = NodeKind.CartogramGroup } };
                foreach (var cartogram in Snapshot.Cartograms)
                {
                    var info = cartogram.Info;
                    string state = !info.Built ? "  (не построена)"
                        : cartogram.Outdated ? "  (УСТАРЕЛА)"
                        : "  +" + Math.Round(info.LastFill).ToString(System.Globalization.CultureInfo.InvariantCulture) + " / −" +
                          Math.Round(info.LastCut).ToString(System.Globalization.CultureInfo.InvariantCulture) + " м³";
                    var node = new TreeNode(info.Name + state)
                    {
                        Tag = new NodeTag { Kind = NodeKind.Cartogram, SurfaceId = info.Id },
                        ForeColor = cartogram.Outdated ? Color.Firebrick : tree.ForeColor,
                    };
                    cartograms.Nodes.Add(node);
                    if (((NodeTag)node.Tag).Key == selectedKey) toSelect = node;
                }
                tree.Nodes.Add(cartograms);
                cartograms.Expand();
                if (toSelect != null) tree.SelectedNode = toSelect;
            }
            finally
            {
                tree.EndUpdate();
                rebuilding = false;
            }
        }

        private void OnNodeSelected(TreeNode node)
        {
            if (!(node?.Tag is NodeTag tag)) return;
            selectedKey = tag.Key;
            switch (tag.Kind)
            {
                case NodeKind.Surface: Display(surfaceView, tag.SurfaceId); break;
                case NodeKind.Points: Display(pointsView, tag.SurfaceId); break;
                case NodeKind.Pairs: Display(pairsView, tag.SurfaceId); break;
                case NodeKind.Cartogram: Display(cartogramView, tag.SurfaceId); break;
                case NodeKind.CartogramGroup: ShowNewCartogram(); break;
                default: Display(null, null); break;
            }
        }

        private void Select(NodeKind kind, string surfaceId)
        {
            if (surfaceId == null) return;
            selectedKey = new NodeTag { Kind = kind, SurfaceId = surfaceId }.Key;
            TreeNode target = null;
            foreach (TreeNode group in tree.Nodes)
            foreach (TreeNode node in group.Nodes)
            {
                if (((NodeTag)node.Tag).Key == selectedKey) target = node;
                if (node.Tag is NodeTag nodeTag && nodeTag.Kind == NodeKind.Cartogram) continue;
                foreach (TreeNode child in node.Nodes)
                    if (((NodeTag)child.Tag).Key == selectedKey) target = child;
            }
            if (target == null) return;
            // AfterSelect does not fire for the node that is already selected.
            if (ReferenceEquals(tree.SelectedNode, target)) OnNodeSelected(target);
            else tree.SelectedNode = target;
        }

        internal void ShowSurface(string surfaceId) => Select(NodeKind.Surface, surfaceId);

        internal void ShowPoints(string surfaceId) => Select(NodeKind.Points, surfaceId);

        internal void ShowNewSurface()
        {
            surfaceView.PrepareNew();
            Display(surfaceView, null);
        }

        internal void ShowImport(string surfaceId)
        {
            importView.Preselect(surfaceId ?? SelectedSurfaceId);
            Display(importView, surfaceId);
        }

        internal void ShowSettings() => Display(settingsView, null);

        internal void ShowNewCartogram()
        {
            cartogramView.PrepareNew();
            Display(cartogramView, null);
        }

        internal void ShowCartogram(string cartogramId)
        {
            if (cartogramId == null) { ShowNewCartogram(); return; }
            Select(NodeKind.Cartogram, cartogramId);
        }

        /// <summary>After a cartogram was saved, built or deleted.</summary>
        internal void AfterCartogramChange(string cartogramId)
        {
            selectedKey = cartogramId == null ? null : new NodeTag { Kind = NodeKind.Cartogram, SurfaceId = cartogramId }.Key;
            RefreshAll();
            if (cartogramId != null) Select(NodeKind.Cartogram, cartogramId);
            else Display(null, null);
        }

        private void Display(Control view, string surfaceId)
        {
            if (shown is PairsView pairs && !ReferenceEquals(view, pairs)) pairs.ClearLabels();
            detail.SuspendLayout();
            detail.Controls.Clear();
            shown = view;
            shownSurfaceId = surfaceId;
            if (view != null)
            {
                view.Dock = DockStyle.Fill;
                detail.Controls.Add(view);
                ((IPanelView)view).Bind(this, surfaceId);
            }
            else
            {
                detail.Controls.Add(new Label
                {
                    Text = Snapshot.Surfaces.Count == 0
                        ? "В чертеже нет поверхностей.\nНажмите «+ Поверхность», затем «Импорт»."
                        : "Выберите поверхность в списке.\nПравый щелчок по поверхности — действия.",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                });
            }
            detail.ResumeLayout();
        }

        internal void SetCurrent(string surfaceId)
        {
            if (surfaceId == null) return;
            PanelActions.Run("текущая поверхность", document => EditServices.SetCurrent(document.Database, surfaceId), RefreshAll);
        }

        private void ClearMarks()
        {
            PanelActions.Run("очистка пометок", document =>
            {
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    int count = Drawing.CheckMarks.Clear(document.Database, transaction);
                    transaction.Commit();
                    PanelMessages.Info("Удалено пометок проверки: " + count + ".");
                }
            });
        }

        /// <summary>After a surface was created or deleted: show it (or nothing) in the tree.</summary>
        internal void AfterSurfaceChange(string surfaceId)
        {
            selectedKey = surfaceId == null ? null : new NodeTag { Kind = NodeKind.Surface, SurfaceId = surfaceId }.Key;
            RefreshAll();
            if (surfaceId != null) Select(NodeKind.Surface, surfaceId);
            else Display(null, null);
        }
    }
}
