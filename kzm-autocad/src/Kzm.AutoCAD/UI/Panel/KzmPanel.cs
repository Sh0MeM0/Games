using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Kzm.AutoCAD.Drawing;
using Kzm.AutoCAD.Services;
using Kzm.Core;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;
using Font = System.Drawing.Font;

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
    /// («Фактические» / «Проектные» → поверхность → «Точки», «Близкие точки»), cartograms
    /// and the detail area for the selected node, the import form or the settings.
    /// With nothing selected the detail area shows «Порядок работы».
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

        // Icons of the tree, drawn at run time (no image files in the bundle).
        private const int IconGroup = 0, IconFact = 1, IconProject = 2, IconPoints = 3, IconWarning = 4, IconCartogram = 5, IconOutdated = 6, IconDraft = 7;

        private readonly TreeView tree = new TreeView();
        private readonly System.Windows.Forms.Panel detail = new System.Windows.Forms.Panel();
        private readonly Label status = new Label();
        private readonly SurfaceView surfaceView = new SurfaceView();
        private readonly PointsView pointsView = new PointsView();
        private readonly PairsView pairsView = new PairsView();
        private readonly ImportView importView = new ImportView();
        private readonly SettingsView settingsView = new SettingsView();
        private readonly CartogramView cartogramView = new CartogramView();
        private readonly LineView lineView = new LineView();
        private readonly PlaneView planeView = new PlaneView();
        private readonly StartView startView = new StartView();
        private readonly ToolStripButton updateButton;
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
            updateButton = Button("Пересчитать", "Пересчитать устаревшие картограммы (KZM_UPDATE)", UpdateOutdated);
            toolbar.Items.Add(updateButton);
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(Button("Настройки", "Настройки чертежа и шаблон по умолчанию", ShowSettings));
            toolbar.Items.Add(Button("Убрать пометки", "Удалить пометки проверки со слоя КЗМ_ПРОВЕРКА", ClearMarks));
            toolbar.Items.Add(Button("Обновить", "Перечитать данные чертежа", RefreshAll));
            toolbar.Items.Add(Button("?", "Порядок работы и подсказки", ShowStart));

            // Size first: WinForms rejects a SplitterDistance larger than the default 150×100 control.
            var split = new SplitContainer { Size = new Size(380, 900), Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel1 };
            split.SplitterDistance = 190;
            split.Dock = DockStyle.Fill;
            tree.Dock = DockStyle.Fill;
            tree.HideSelection = false;
            tree.ShowNodeToolTips = true;
            tree.ImageList = Icons();
            tree.AfterSelect += (s, e) => { if (!rebuilding) OnNodeSelected(e.Node); };
            tree.NodeMouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                tree.SelectedNode = e.Node;
                var menu = MenuFor(e.Node.Tag as NodeTag);
                if (menu != null) menu.Show(tree, e.Location);
            };
            tree.NodeMouseDoubleClick += (s, e) => ShowOnDrawing(e.Node?.Tag as NodeTag);
            tree.KeyDown += (s, e) =>
            {
                var tag = tree.SelectedNode?.Tag as NodeTag;
                if (e.KeyCode == Keys.Delete && tag != null) { DeleteNode(tag); e.Handled = true; }
                if (e.KeyCode == Keys.Enter && tag != null) { ShowOnDrawing(tag); e.Handled = true; }
            };
            split.Panel1.Controls.Add(tree);
            detail.Dock = DockStyle.Fill;
            detail.AutoScroll = true;
            split.Panel2.Controls.Add(detail);

            status.Dock = DockStyle.Bottom;
            status.AutoSize = false;
            status.Height = 40;
            status.Padding = new Padding(4, 2, 4, 2);
            status.TextAlign = ContentAlignment.MiddleLeft;

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

        internal string SelectedSurfaceId
        {
            get
            {
                var tag = tree.SelectedNode?.Tag as NodeTag;
                return tag != null && tag.Kind != NodeKind.Cartogram ? tag.SurfaceId ?? Snapshot.CurrentId : Snapshot.CurrentId;
            }
        }

        private static ToolStripButton Button(string text, string tip, Action action)
        {
            var button = new ToolStripButton(text) { ToolTipText = tip, DisplayStyle = ToolStripItemDisplayStyle.Text };
            button.Click += (s, e) => action();
            return button;
        }

        private static ImageList Icons()
        {
            var list = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
            Bitmap Draw(Action<Graphics> paint)
            {
                var bitmap = new Bitmap(16, 16);
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    paint(g);
                }
                return bitmap;
            }
            list.Images.Add(Draw(g =>
            {
                g.FillRectangle(Brushes.Khaki, 1, 4, 14, 10);
                g.DrawRectangle(Pens.Peru, 1, 4, 14, 10);
                g.FillRectangle(Brushes.Peru, 1, 2, 6, 3);
            }));
            list.Images.Add(Draw(g =>
            {
                g.FillPolygon(Brushes.SaddleBrown, new[] { new Point(1, 13), new Point(5, 6), new Point(9, 10), new Point(13, 4), new Point(15, 13) });
            }));
            list.Images.Add(Draw(g =>
            {
                g.FillPolygon(Brushes.IndianRed, new[] { new Point(1, 13), new Point(1, 8), new Point(15, 5), new Point(15, 13) });
                g.DrawLine(Pens.DarkRed, 1, 8, 15, 5);
            }));
            list.Images.Add(Draw(g =>
            {
                foreach (var p in new[] { new Point(3, 4), new Point(10, 3), new Point(6, 9), new Point(12, 11), new Point(3, 13) })
                    g.FillEllipse(Brushes.DimGray, p.X - 2, p.Y - 2, 4, 4);
            }));
            list.Images.Add(Draw(g =>
            {
                g.FillPolygon(Brushes.Orange, new[] { new Point(8, 1), new Point(15, 14), new Point(1, 14) });
                g.DrawString("!", new Font(FontFamily.GenericSansSerif, 7, FontStyle.Bold), Brushes.Black, 5, 4);
            }));
            Bitmap Grid(Brush fill) => Draw(g =>
            {
                g.FillRectangle(fill, 2, 2, 12, 12);
                for (int i = 2; i <= 14; i += 4)
                {
                    g.DrawLine(Pens.Gray, i, 2, i, 14);
                    g.DrawLine(Pens.Gray, 2, i, 14, i);
                }
            });
            list.Images.Add(Grid(Brushes.Honeydew));
            list.Images.Add(Grid(Brushes.MistyRose));
            list.Images.Add(Grid(Brushes.WhiteSmoke));
            return list;
        }

        /// <summary>Re-reads the drawing and redraws the tree and the open view.</summary>
        internal void RefreshAll()
        {
            Document = AcadApplication.DocumentManager.MdiActiveDocument;
            Snapshot = DrawingData.Read(Document);
            if (Snapshot.Error != null) PanelMessages.Error("данные КЗМ в чертеже не читаются: " + Snapshot.Error);
            int outdated = Snapshot.Cartograms.Count(c => c.Outdated);
            updateButton.Enabled = outdated > 0;
            updateButton.Text = outdated > 0 ? "Пересчитать (" + outdated + ")" : "Пересчитать";
            RebuildTree();
            if (shown is IPanelView view) view.Bind(this, shownSurfaceId);
            else if (shown == null) Display(null, null);
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
                TreeNode Node(string text, NodeTag tag, int icon, string tip = null, Color? color = null)
                {
                    var node = new TreeNode(text) { Tag = tag, ImageIndex = icon, SelectedImageIndex = icon, ToolTipText = tip };
                    if (color.HasValue) node.ForeColor = color.Value;
                    if (tag.Key == selectedKey) toSelect = node;
                    return node;
                }
                foreach (var kind in new[] { SurfaceKind.Fact, SurfaceKind.Project })
                {
                    var surfaces = Snapshot.Surfaces.Where(surface => surface.Info.Kind == kind).ToList();
                    var group = Node((kind == SurfaceKind.Fact ? "Фактические" : "Проектные") + " (" + surfaces.Count + ")",
                        new NodeTag { Kind = NodeKind.Group, Group = kind }, IconGroup, "Правый щелчок — создать поверхность");
                    foreach (var surface in surfaces)
                    {
                        string extra = (surface.StructureLines > 0 ? ", линий " + surface.StructureLines : string.Empty) +
                                       (surface.ExcludedPoints > 0 ? ", искл. " + surface.ExcludedPoints : string.Empty);
                        var node = Node(surface.Info.Name + "  —  " + surface.Points + " т." + extra + (surface.IsCurrent ? "  (текущая)" : string.Empty),
                            new NodeTag { Kind = NodeKind.Surface, SurfaceId = surface.Info.Id }, kind == SurfaceKind.Fact ? IconFact : IconProject,
                            "Слой " + surface.Info.Layer + ". Двойной щелчок — показать точки на чертеже, правый — действия.",
                            surface.TrianglesOutdated ? Color.DarkOrange : (Color?)null);
                        node.Nodes.Add(Node("Точки (" + surface.Points + (surface.BrokenPoints > 0 ? ", с ошибкой " + surface.BrokenPoints : string.Empty) + ")",
                            new NodeTag { Kind = NodeKind.Points, SurfaceId = surface.Info.Id }, surface.BrokenPoints > 0 ? IconWarning : IconPoints,
                            "Таблица точек: номер и отметку можно править прямо в ней", surface.BrokenPoints > 0 ? Color.Firebrick : (Color?)null));
                        if (surface.PendingPairs > 0)
                            node.Nodes.Add(Node("Близкие точки (" + surface.PendingPairs + ")", new NodeTag { Kind = NodeKind.Pairs, SurfaceId = surface.Info.Id },
                                IconWarning, "Точки ближе 0,01 м друг к другу: решение за вами", Color.Firebrick));
                        group.Nodes.Add(node);
                        node.Expand();
                    }
                    tree.Nodes.Add(group);
                    group.Expand();
                }
                var cartograms = Node("Картограммы (" + Snapshot.Cartograms.Count + ")", new NodeTag { Kind = NodeKind.CartogramGroup }, IconGroup,
                    "Правый щелчок — новая картограмма");
                foreach (var cartogram in Snapshot.Cartograms)
                {
                    var info = cartogram.Info;
                    string state = !info.Built ? "  (не построена)"
                        : cartogram.Outdated ? "  (УСТАРЕЛА)"
                        : "  +" + Math.Round(info.LastFill).ToString(System.Globalization.CultureInfo.InvariantCulture) + " / −" +
                          Math.Round(info.LastCut).ToString(System.Globalization.CultureInfo.InvariantCulture) + " м³";
                    cartograms.Nodes.Add(Node(info.Name + state, new NodeTag { Kind = NodeKind.Cartogram, SurfaceId = info.Id },
                        !info.Built ? IconDraft : cartogram.Outdated ? IconOutdated : IconCartogram,
                        cartogram.Outdated ? "Точки, границы или параметры изменились после построения — «Пересчитать»"
                            : "Двойной щелчок — показать на чертеже, правый — действия",
                        cartogram.Outdated ? Color.Firebrick : (Color?)null));
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
                default: Display(null, null); break;
            }
        }

        // ---------- context menus, double click, Delete ----------

        private ContextMenuStrip MenuFor(NodeTag tag)
        {
            if (tag == null) return null;
            var menu = new ContextMenuStrip();
            void Item(string text, Action action, bool enabled = true)
            {
                var item = new ToolStripMenuItem(text) { Enabled = enabled };
                item.Click += (s, e) => action();
                menu.Items.Add(item);
            }
            switch (tag.Kind)
            {
                case NodeKind.Group:
                    Item(tag.Group == SurfaceKind.Fact ? "Новая фактическая поверхность…" : "Новая проектная поверхность…", () => ShowNewSurface(tag.Group));
                    break;
                case NodeKind.CartogramGroup:
                    Item("Новая картограмма…", ShowNewCartogram);
                    if (Snapshot.Cartograms.Any(c => c.Outdated)) Item("Пересчитать устаревшие", UpdateOutdated);
                    break;
                case NodeKind.Surface:
                case NodeKind.Points:
                case NodeKind.Pairs:
                {
                    var surface = Snapshot.Find(tag.SurfaceId);
                    if (surface == null) return null;
                    string id = surface.Info.Id;
                    Item("Свойства", () => ShowSurface(id));
                    Item("Таблица точек", () => ShowPoints(id));
                    menu.Items.Add(new ToolStripSeparator());
                    Item("Импорт отметок…", () => ShowImport(id));
                    Item("Создать точки вручную…", () => surfaceView.CreateByHand(this, id));
                    Item("Плоскость…", () => ShowPlane(id));
                    Item("Структурная линия по полилинии…", () => LineView.PickPolyline(this, id));
                    menu.Items.Add(new ToolStripSeparator());
                    Item(surface.Info.ShowTriangles ? "Скрыть треугольники" : "Показать треугольники", () => surfaceView.ToggleTriangles(this, id, !surface.Info.ShowTriangles));
                    Item("Показать точки на чертеже", () => ShowOnDrawing(tag));
                    Item("Сделать текущей", () => SetCurrent(id), !surface.IsCurrent);
                    menu.Items.Add(new ToolStripSeparator());
                    Item("Удалить поверхность…", () => DeleteNode(new NodeTag { Kind = NodeKind.Surface, SurfaceId = id }));
                    break;
                }
                case NodeKind.Cartogram:
                {
                    var cartogram = Snapshot.FindCartogram(tag.SurfaceId);
                    if (cartogram == null) return null;
                    string id = cartogram.Info.Id;
                    Item("Свойства", () => ShowCartogram(id));
                    Item(cartogram.Info.Built ? "Пересчитать" : "Построить", () => cartogramView.Build(this, id));
                    Item("Показать на чертеже", () => ShowOnDrawing(tag), cartogram.Info.Built);
                    Item("В Excel…", () => cartogramView.Export(this, id), cartogram.Info.Built);
                    Item("Ведомость объёмов…", () => cartogramView.Statement(this, id), cartogram.Info.Built);
                    menu.Items.Add(new ToolStripSeparator());
                    Item("Удалить картограмму…", () => DeleteNode(tag));
                    break;
                }
            }
            return menu.Items.Count > 0 ? menu : null;
        }

        private void ShowOnDrawing(NodeTag tag)
        {
            if (tag == null || Document == null) return;
            try
            {
                if (tag.Kind == NodeKind.Surface || tag.Kind == NodeKind.Points)
                {
                    var points = DrawingData.ReadPoints(Document, Snapshot.Settings, tag.SurfaceId);
                    if (points.Count == 0) { PanelMessages.Error("у поверхности нет точек."); return; }
                    double size = Commands.KzmCommands.ZoomSize(Snapshot.Find(tag.SurfaceId)?.Info.Appearance.TextHeight);
                    // Thousands of selected blocks slow AutoCAD down: large surfaces are only zoomed to.
                    if (points.Count <= 2000) PanelActions.Show(Document, points.Select(p => p.Id), size);
                    else PanelActions.ShowArea(Document, points.Select(p => p.Id), size);
                    PanelMessages.Info("Точек: " + points.Count + (points.Count <= 2000 ? " (выделены на чертеже)." : "."));
                }
                else if (tag.Kind == NodeKind.Cartogram)
                {
                    var c = Snapshot.FindCartogram(tag.SurfaceId);
                    if (c == null || !c.Info.Built) { PanelMessages.Error("картограмма ещё не построена."); return; }
                    List<ObjectId> ids;
                    using (Document.LockDocument(DocumentLockMode.Read, null, null, false))
                    using (var transaction = Document.Database.TransactionManager.StartOpenCloseTransaction())
                    {
                        ids = CartogramService.ObjectsOf(Document.Database, transaction, c.Info.Id);
                        transaction.Commit();
                    }
                    if (ids.Count == 0) { PanelMessages.Error("объектов картограммы нет на чертеже (стёрты?)."); return; }
                    PanelActions.ShowArea(Document, ids, 20);
                }
            }
            catch (KzmException exception)
            {
                PanelMessages.Error(exception.Message);
            }
        }

        private void DeleteNode(NodeTag tag)
        {
            if (tag == null) return;
            if (tag.Kind == NodeKind.Surface) surfaceView.Delete(this, tag.SurfaceId);
            else if (tag.Kind == NodeKind.Cartogram) cartogramView.Delete(this, tag.SurfaceId);
        }

        // ---------- navigation ----------

        private void Select(NodeKind kind, string surfaceId)
        {
            if (surfaceId == null) return;
            selectedKey = new NodeTag { Kind = kind, SurfaceId = surfaceId }.Key;
            TreeNode target = null;
            foreach (TreeNode group in tree.Nodes)
            foreach (TreeNode node in group.Nodes)
            {
                if (((NodeTag)node.Tag).Key == selectedKey) target = node;
                foreach (TreeNode child in node.Nodes)
                    if (((NodeTag)child.Tag).Key == selectedKey) target = child;
            }
            if (target == null) return;
            // AfterSelect does not fire for the node that is already selected.
            if (ReferenceEquals(tree.SelectedNode, target)) OnNodeSelected(target);
            else tree.SelectedNode = target;
        }

        internal void ShowSurface(string surfaceId)
        {
            if (surfaceId == null) { ShowStart(); return; }
            Select(NodeKind.Surface, surfaceId);
        }

        internal void ShowPoints(string surfaceId) => Select(NodeKind.Points, surfaceId);

        internal void ShowNewSurface(SurfaceKind? kind = null)
        {
            surfaceView.PrepareNew(kind);
            Display(surfaceView, null);
        }

        internal void ShowImport(string surfaceId)
        {
            importView.Preselect(surfaceId ?? SelectedSurfaceId);
            Display(importView, surfaceId);
        }

        internal void ShowSettings() => Display(settingsView, null);

        internal void ShowStart()
        {
            tree.SelectedNode = null;
            selectedKey = null;
            Display(null, null);
        }

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

        internal void ShowLineDraft(LineDraft draft)
        {
            lineView.Edit(draft);
            Display(lineView, draft.SurfaceId);
        }

        internal void ShowPlane(string surfaceId)
        {
            if (surfaceId == null) return;
            Display(planeView, surfaceId);
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
            if (shown is CartogramView cartogram && !ReferenceEquals(view, cartogram)) cartogram.ClearPreview();
            view = view ?? startView;
            detail.SuspendLayout();
            detail.Controls.Clear();
            shown = view;
            shownSurfaceId = surfaceId;
            view.Dock = DockStyle.Fill;
            detail.Controls.Add(view);
            ((IPanelView)view).Bind(this, surfaceId);
            detail.ResumeLayout();
        }

        internal void SetCurrent(string surfaceId)
        {
            if (surfaceId == null) return;
            PanelActions.Run("текущая поверхность", document => EditServices.SetCurrent(document.Database, surfaceId), RefreshAll);
        }

        /// <summary>KZM_UPDATE from the panel: rebuild all outdated cartograms.</summary>
        internal void UpdateOutdated()
        {
            PanelActions.Run("пересчёт картограмм", document =>
            {
                int count = Commands.KzmCommands.UpdateOutdated(document);
                PanelMessages.Info(count == 0 ? "Устаревших картограмм нет." : "Пересчитано картограмм: " + count + " (итоги — в командной строке).");
            }, RefreshAll);
        }

        private void ClearMarks()
        {
            PanelActions.Run("очистка пометок", document =>
            {
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    int count = CheckMarks.Clear(document.Database, transaction);
                    transaction.Commit();
                    PanelMessages.Info("Удалено пометок проверки: " + count + ".");
                }
            });
        }

        /// <summary>After a surface was created or deleted: show it (or the start page) in the tree.</summary>
        internal void AfterSurfaceChange(string surfaceId)
        {
            selectedKey = surfaceId == null ? null : new NodeTag { Kind = NodeKind.Surface, SurfaceId = surfaceId }.Key;
            RefreshAll();
            if (surfaceId != null) Select(NodeKind.Surface, surfaceId);
            else Display(null, null);
        }
    }
}
