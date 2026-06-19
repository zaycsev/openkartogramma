using System;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Civil.ApplicationServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using SystemVariableChangedEventArgs = Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventArgs;

// Регистрируем точку входа плагина — AutoCAD вызовет Initialize() при загрузке DLL
[assembly: ExtensionApplication(typeof(KartogrammaPlugin.AppInit))]
[assembly: CommandClass(typeof(KartogrammaPlugin.AppInit))]

namespace KartogrammaPlugin
{
    public sealed class AppInit : IExtensionApplication
    {
        private static bool _ribbonReady = false;

        // ── Статический конструктор: выполняется при первой загрузке типа CLR ───
        static AppInit()
        {
            Log("=== AppInit static constructor — DLL loaded by CLR ===");
        }

        // ── Вызывается AutoCAD один раз при загрузке DLL ────────────────────────
        public void Initialize()
        {
            Log("Initialize() called");
            try
            {
                // Явно регистрируем сборку — на случай если AutoCAD пропустил сканирование атрибутов
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    Log($"Assembly: {asm.FullName}");
                    Log($"Assembly location: {asm.Location}");

                    // Перечислим все методы с [CommandMethod] для диагностики
                    int cmdCount = 0;
                    foreach (var type in asm.GetTypes())
                    {
                        foreach (var method in type.GetMethods())
                        {
                            var attrs = method.GetCustomAttributes(typeof(CommandMethodAttribute), false);
                            if (attrs.Length > 0)
                            {
                                var attr = (CommandMethodAttribute)attrs[0];
                                Log($"  Found [CommandMethod]: {type.Name}.{method.Name} → GlobalName={attr.GlobalName}");
                                cmdCount++;
                            }
                        }
                    }
                    Log($"Total [CommandMethod] found via reflection: {cmdCount}");
                }
                catch (System.Exception ex2)
                {
                    Log($"Reflection scan error: {ex2.Message}");
                }

                Log("Checking ribbon...");
                if (ComponentManager.Ribbon != null)
                {
                    Log("Ribbon ready, adding button");
                    AddRibbonButton();
                }
                else
                {
                    Log("Ribbon not ready, subscribing to ItemInitialized + Idle");
                    ComponentManager.ItemInitialized += OnRibbonItemInitialized;
                    // Запасной путь: если лента инициализировалась ДО загрузки плагина,
                    // событие ItemInitialized уже не сработает. Application.Idle гарантирует
                    // вызов, как только AutoCAD освободится и лента будет доступна.
                    Application.Idle += OnIdleAddRibbon;
                }

                Application.SystemVariableChanged += OnSysVarChanged;

                Log("Initialize() done");
                WriteToCmd("Плагин «Картограмма» загружен. Команда: OpenKartogramma");
            }
            catch (System.Exception ex)
            {
                Log($"EXCEPTION in Initialize: {ex}");
                WriteToCmd($"Предупреждение при инициализации ленты: {ex.Message}");
            }
        }

        public void Terminate()
        {
            Application.SystemVariableChanged -= OnSysVarChanged;
        }

        // ── Простая тестовая команда (без Civil 3D зависимостей) ─────────────────
        [CommandMethod("KHELLO")]
        public void RunKHello()
        {
            Log("KHELLO command executed");
            var doc = Application.DocumentManager.MdiActiveDocument;
            doc?.Editor.WriteMessage("\n[KartogrammaPlugin] KHELLO работает! DLL загружена корректно.\n");
        }

        // ── Команда OpenKartogramma ──────────────────────────────────────────────
        [CommandMethod("OpenKartogramma", CommandFlags.Modal)]
        public void RunKartogramma()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var surfaceNames = new List<string>();
            var textStyles = new List<string>();
            try
            {
                var civilDoc = CivilApplication.ActiveDocument;
                using var trans = doc.Database.TransactionManager.StartTransaction();
                foreach (ObjectId id in civilDoc.GetSurfaceIds())
                {
                    if (trans.GetObject(id, OpenMode.ForRead) is CivilSurface s)
                        surfaceNames.Add(s.Name);
                }
                var tst = (TextStyleTable)trans.GetObject(
                    doc.Database.TextStyleTableId, OpenMode.ForRead);
                foreach (ObjectId tsId in tst)
                {
                    if (trans.GetObject(tsId, OpenMode.ForRead) is TextStyleTableRecord ts
                        && !ts.Name.StartsWith("*"))
                        textStyles.Add(ts.Name);
                }
                trans.Commit();
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[Картограмма] Ошибка чтения документа: {ex.Message}");
                return;
            }
            if (surfaceNames.Count < 2)
            {
                ed.WriteMessage("\n[Картограмма] В чертеже должно быть минимум 2 поверхности Civil 3D.");
                Application.ShowAlertDialog(
                    "В чертеже должно быть минимум 2 поверхности Civil 3D.\n" +
                    "Добавьте поверхности и повторите команду.");
                return;
            }
            if (textStyles.Count == 0) textStyles.Add("Standard");

            // Имя документа для заголовка диалога
            string docTitle = System.IO.Path.GetFileNameWithoutExtension(doc.Name);

            // ── Главный диалог — цикл с поддержкой pick в чертеже ──────────────
            double pickedAngle = double.NaN;
            double pickedBaseX = double.NaN, pickedBaseY = double.NaN;
            double pickedSizeX = double.NaN, pickedSizeY = double.NaN;
            Autodesk.AutoCAD.DatabaseServices.ObjectId pickedOuterBoundary =
                Autodesk.AutoCAD.DatabaseServices.ObjectId.Null;
            System.Collections.Generic.List<Autodesk.AutoCAD.DatabaseServices.ObjectId>? pickedInnerBoundaries = null;

            while (true)
            {
                using var mainForm = new MainKartogrammaForm(doc, surfaceNames, textStyles, docTitle);

                // Применить результат предыдущего pick (если был)
                if (!double.IsNaN(pickedAngle))
                {
                    mainForm.ApplyPickedAngle(pickedAngle);
                    pickedAngle = double.NaN;
                }
                if (!double.IsNaN(pickedBaseX))
                {
                    mainForm.ApplyPickedBase(pickedBaseX, pickedBaseY);
                    pickedBaseX = pickedBaseY = double.NaN;
                }
                if (!pickedOuterBoundary.IsNull)
                {
                    mainForm.ApplyPickedOuterBoundary(pickedOuterBoundary);
                    pickedOuterBoundary = Autodesk.AutoCAD.DatabaseServices.ObjectId.Null;
                }
                if (pickedInnerBoundaries != null)
                {
                    mainForm.ApplyPickedInnerBoundaries(pickedInnerBoundaries);
                    pickedInnerBoundaries = null;
                }
                if (!double.IsNaN(pickedSizeX))
                {
                    mainForm.ApplyPickedSizeX(pickedSizeX);
                    pickedSizeX = double.NaN;
                }
                if (!double.IsNaN(pickedSizeY))
                {
                    mainForm.ApplyPickedSizeY(pickedSizeY);
                    pickedSizeY = double.NaN;
                }

                Application.ShowModalDialog(null, mainForm, false);

                // Если диалог запросил pick — выполняем и открываем заново
                if (mainForm.DialogResult == System.Windows.Forms.DialogResult.Retry)
                {
                    switch (mainForm.PendingPick)
                    {
                        case PickRequest.Angle:
                            var p1 = ed.GetPoint("\nПервая точка для определения угла: ");
                            if (p1.Status != PromptStatus.OK) continue;
                            var opts2 = new PromptPointOptions("\nВторая точка: ")
                            { BasePoint = p1.Value, UseBasePoint = true };
                            var p2 = ed.GetPoint(opts2);
                            if (p2.Status != PromptStatus.OK) continue;
                            double dx = p2.Value.X - p1.Value.X;
                            double dy = p2.Value.Y - p1.Value.Y;
                            if (Math.Abs(dx) > 1e-9 || Math.Abs(dy) > 1e-9)
                                pickedAngle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                            continue;

                        case PickRequest.BasePoint:
                            var bp = ed.GetPoint("\nУкажите базовую точку сетки: ");
                            if (bp.Status == PromptStatus.OK)
                            {
                                pickedBaseX = bp.Value.X;
                                pickedBaseY = bp.Value.Y;
                            }
                            continue;

                        case PickRequest.OuterBoundary:
                            var peo = new PromptEntityOptions(
                                "\nВыберите замкнутую полилинию наружной границы: ");
                            peo.SetRejectMessage("\nОбъект должен быть полилинией.");
                            peo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline), true);
                            var per = ed.GetEntity(peo);
                            if (per.Status == PromptStatus.OK)
                            {
                                using var tr = doc.Database.TransactionManager.StartTransaction();
                                var pl = tr.GetObject(per.ObjectId,
                                    Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead)
                                    as Autodesk.AutoCAD.DatabaseServices.Polyline;
                                if (pl != null && pl.Closed)
                                    pickedOuterBoundary = per.ObjectId;
                                else
                                    ed.WriteMessage("\nВыбранная полилиния не замкнута.");
                                tr.Commit();
                            }
                            continue;

                        case PickRequest.CopyKartogramma:
                            {
                                // Собираем ВСЕ объекты на актуальных слоях картограммы
                                // (с учётом пользовательских переименований через
                                // диалог-шестерёнку), ставим их как pickfirst-выборку
                                // и запускаем _.COPYBASE — это эквивалент Ctrl+Shift+C:
                                // запросит базовую точку и положит выборку в буфер
                                // обмена, чтобы её можно было вставить в другой чертёж.
                                var kartLayers = new HashSet<string>(
                                    MainKartogrammaForm.CurrentLayerNames,
                                    StringComparer.OrdinalIgnoreCase);
                                var ids = new List<ObjectId>();
                                using (var trc = doc.Database.TransactionManager.StartTransaction())
                                {
                                    var bt = (BlockTable)trc.GetObject(
                                        doc.Database.BlockTableId, OpenMode.ForRead);
                                    var ms = (BlockTableRecord)trc.GetObject(
                                        bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                                    foreach (ObjectId entId in ms)
                                    {
                                        var ent = trc.GetObject(entId, OpenMode.ForRead) as Entity;
                                        if (ent == null) continue;
                                        if (kartLayers.Contains(ent.Layer))
                                            ids.Add(entId);
                                    }
                                    trc.Commit();
                                }
                                if (ids.Count == 0)
                                {
                                    ed.WriteMessage("\n[Картограмма] На слоях картограммы нет объектов для копирования.");
                                    continue;
                                }
                                ed.SetImpliedSelection(ids.ToArray());
                                doc.SendStringToExecute("_.COPYBASE ", true, false, true);
                                // Не открываем диалог снова — пользователь работает в чертеже.
                                return;
                            }

                        case PickRequest.InnerBoundaries:
                            {
                                var collected = new System.Collections.Generic.List<Autodesk.AutoCAD.DatabaseServices.ObjectId>();
                                ed.WriteMessage("\nВыберите замкнутые полилинии внутренних границ (ENTER — закончить).");
                                while (true)
                                {
                                    var pei = new PromptEntityOptions(
                                        $"\nВнутренняя граница #{collected.Count + 1} (ENTER — готово): ");
                                    pei.SetRejectMessage("\nОбъект должен быть полилинией.");
                                    pei.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline), true);
                                    pei.AllowNone = true;
                                    var peri = ed.GetEntity(pei);
                                    if (peri.Status != PromptStatus.OK) break;
                                    using var tri = doc.Database.TransactionManager.StartTransaction();
                                    var pli = tri.GetObject(peri.ObjectId,
                                        Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead)
                                        as Autodesk.AutoCAD.DatabaseServices.Polyline;
                                    if (pli != null && pli.Closed)
                                    {
                                        if (!collected.Contains(peri.ObjectId))
                                            collected.Add(peri.ObjectId);
                                        else
                                            ed.WriteMessage("\nЭта полилиния уже добавлена.");
                                    }
                                    else
                                    {
                                        ed.WriteMessage("\nВыбранная полилиния не замкнута.");
                                    }
                                    tri.Commit();
                                }
                                pickedInnerBoundaries = collected;
                            }
                            continue;

                        case PickRequest.SizeX:
                            {
                                var sp1 = ed.GetPoint("\nПервая точка для размера по горизонтали: ");
                                if (sp1.Status != PromptStatus.OK) continue;
                                var spo = new PromptPointOptions("\nВторая точка: ")
                                { BasePoint = sp1.Value, UseBasePoint = true };
                                var sp2 = ed.GetPoint(spo);
                                if (sp2.Status != PromptStatus.OK) continue;
                                double dist = Math.Abs(sp2.Value.X - sp1.Value.X);
                                if (dist > 0.01) pickedSizeX = Math.Round(dist, 2);
                            }
                            continue;

                        case PickRequest.SizeY:
                            {
                                var sp1 = ed.GetPoint("\nПервая точка для размера по вертикали: ");
                                if (sp1.Status != PromptStatus.OK) continue;
                                var spo = new PromptPointOptions("\nВторая точка: ")
                                { BasePoint = sp1.Value, UseBasePoint = true };
                                var sp2 = ed.GetPoint(spo);
                                if (sp2.Status != PromptStatus.OK) continue;
                                double dist = Math.Abs(sp2.Value.Y - sp1.Value.Y);
                                if (dist > 0.01) pickedSizeY = Math.Round(dist, 2);
                            }
                            continue;

                        case PickRequest.CalloutLabel:
                            {
                                var opts = mainForm.CollectOptionsPublic();
                                ed.WriteMessage("\nВыноска отметок: нажмите на любую цифру тройки (ENTER — выход).");
                                var proc = new KartogrammaProcessor(doc, opts);
                                while (true)
                                {
                                    // Шаг 1: пользователь кликает на любую цифру тройки
                                    var po1 = new PromptPointOptions(
                                        "\nНажмите на любую цифру тройки (ENTER — выход): ")
                                    { AllowNone = true };
                                    var pr1 = ed.GetPoint(po1);
                                    if (pr1.Status != PromptStatus.OK) break;

                                    // Шаг 2: из реальных позиций меток вычисляем
                                    // точный узел сетки — откуда стартует выноска
                                    var nodePos = proc.FindTripletOrigin(pr1.Value);
                                    if (nodePos == null)
                                    {
                                        ed.WriteMessage("\n[Картограмма] Рядом с указанной точкой не найдены отметки.");
                                        continue;
                                    }

                                    // Шаг 3: пользователь выбирает новое место —
                                    // резинка тянется от узла (центра тройки)
                                    var po2 = new PromptPointOptions("\nНовое положение тройки: ")
                                    { BasePoint = nodePos.Value, UseBasePoint = true };
                                    var pr2 = ed.GetPoint(po2);
                                    if (pr2.Status != PromptStatus.OK) break;

                                    using (doc.LockDocument())
                                        proc.CreateLabelCallout(nodePos.Value, pr2.Value);
                                }
                            }
                            continue;
                    }
                }

                // Любой другой DialogResult → выходим из цикла
                break;
            }
            ed.WriteMessage("\n[Картограмма] Диалог закрыт.\n");
        }

        // ── Событие: лента полностью инициализирована ───────────────────────────
        private static void OnRibbonItemInitialized(object sender, RibbonItemEventArgs e)
        {
            if (ComponentManager.Ribbon == null) return;
            ComponentManager.ItemInitialized -= OnRibbonItemInitialized;
            Application.Idle -= OnIdleAddRibbon;
            AddRibbonButton();
        }

        // ── Запасной путь через Idle: срабатывает, даже если ItemInitialized
        //    уже не возникнет (лента построена до загрузки плагина) ───────────────
        private static int _idleTicks = 0;
        private static void OnIdleAddRibbon(object? sender, EventArgs e)
        {
            _idleTicks++;
            var ribbon = ComponentManager.Ribbon;

            // Лента ещё не готова — ждём следующего Idle
            if (ribbon == null)
            {
                if (_idleTicks <= 3 || _idleTicks % 30 == 0)
                    Log($"Idle #{_idleTicks}: ribbon still null, waiting...");
                return;
            }

            AddRibbonButton();

            if (_ribbonReady)
            {
                // Успех — отписываемся от обоих источников
                Application.Idle -= OnIdleAddRibbon;
                ComponentManager.ItemInitialized -= OnRibbonItemInitialized;
                Log($"Idle #{_idleTicks}: ribbon button added — unsubscribed");
            }
            else if (_idleTicks <= 5 || _idleTicks % 30 == 0)
            {
                // Лента есть, но кнопку добавить пока не удалось (вкладка не готова) —
                // продолжаем пробовать на следующих Idle
                Log($"Idle #{_idleTicks}: ribbon present but button not added yet, retrying...");
            }
        }

        // ── Событие: смена рабочего пространства (WSCURRENT) ────────────────────
        // При переключении workspace лента пересоздаётся → нужно снова добавить кнопку
        private static void OnSysVarChanged(object sender, SystemVariableChangedEventArgs e)
        {
            if (e.Name.Equals("WSCURRENT", StringComparison.OrdinalIgnoreCase))
            {
                _ribbonReady = false; // сбрасываем флаг — нужно создать заново
                if (ComponentManager.Ribbon != null)
                    AddRibbonButton();
                else
                {
                    ComponentManager.ItemInitialized += OnRibbonItemInitialized;
                    Application.Idle += OnIdleAddRibbon;
                }
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        //  Добавление кнопки на ленту
        // ════════════════════════════════════════════════════════════════════════
        private static void AddRibbonButton()
        {
            if (_ribbonReady) return;

            var ribbon = ComponentManager.Ribbon;
            if (ribbon == null) return;

            // ── Ищем вкладку «Надстройки» / «Add-ins» ───────────────────────────
            RibbonTab? addinsTab = FindAddinsTab(ribbon);
            if (addinsTab == null)
            {
                // Вкладки «Надстройки» нет (или ещё не создана) — создаём именно
                // стандартную вкладку «Надстройки» (системный Id), НЕ собственную.
                Log($"AddRibbonButton: Add-ins tab not found among {ribbon.Tabs.Count} tabs — creating standard Add-ins tab");
                addinsTab = EnsureAddinsTab(ribbon);
            }
            else
            {
                Log($"AddRibbonButton: target tab = '{addinsTab.Title}' (Id={addinsTab.Id})");
            }

            // Если наша панель уже есть — не дублируем
            foreach (RibbonPanel p in addinsTab.Panels)
                if (p.Source?.Id == "KC_PANEL") { _ribbonReady = true; return; }

            // ── Создаём панель ────────────────────────────────────────────────────
            var panelSrc = new RibbonPanelSource
            {
                Id = "KC_PANEL",
                Title = "Картограмма\nземляных работ"
            };

            // ── Большая кнопка (только иконка, текст — в заголовке панели) ────────
            var btnKartogramma = new RibbonButton
            {
                Id = "KC_BTN_KARTOGRAMMA",
                Text = "Картограмма земляных работ",
                ToolTip = MakeToolTip(
                    "Картограмма земляных работ",
                    "Строит сетку квадратов с отметками выемки / насыпи " +
                    "по двум поверхностям Civil 3D.\n\n" +
                    "• Чёрная отметка — существующий рельеф\n" +
                    "• Красная отметка — проектная поверхность\n" +
                    "• Рабочая отметка — разница\n" +
                    "• Объём — м³ на ячейку\n\n" +
                    "Команда: OpenKartogramma"),
                // LargeImage: 32 DIP (128×128 при SCALE=4, 384 DPI) — стандарт ленты AutoCAD.
                // Image: 16 DIP (64×64 при SCALE=4) — маленький вид панели.
                LargeImage = MakeIcon(32),
                Image = MakeIcon(16),
                Size = RibbonItemSize.Large,
                ShowText = false,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                CommandHandler = new AcadCommandHandler("OpenKartogramma")
            };

            panelSrc.Items.Add(btnKartogramma);

            var panel = new RibbonPanel { Source = panelSrc };
            addinsTab.Panels.Add(panel);

            // Показываем вкладку «Надстройки» автоматически — раньше её приходилось
            // включать вручную, т.к. без содержимого она оставалась скрытой.
            addinsTab.IsVisible = true;

            // Двустрочный заголовок панели. Применяем сразу И при каждой активации
            // вкладки: содержимое скрытой вкладки WPF не строит, поэтому, когда панель
            // добавлена в неактивную вкладку, фикс нужно повторить в момент её открытия.
            addinsTab.PropertyChanged -= OnAddinsTabActivated;
            addinsTab.PropertyChanged += OnAddinsTabActivated;
            SchedulePanelTitleFix(ribbon);

            _ribbonReady = true;
            Log($"AddRibbonButton: panel added to '{addinsTab.Title}', _ribbonReady=true");
        }

        // ── Ищем вкладку «Надстройки» по стандартному ID и по заголовку ─────────
        private static RibbonTab? FindAddinsTab(RibbonControl ribbon)
        {
            // Стандартный ID вкладки «Add-ins» в AutoCAD
            const string AddinsTabId = "ID_ADDINSTAB";

            RibbonTab? byTitle = null;

            // Не фильтруем по IsVisible: на раннем этапе загрузки вкладка «Надстройки»
            // может существовать, но быть ещё невидимой — её всё равно нужно найти.
            foreach (RibbonTab tab in ribbon.Tabs)
            {
                if (tab.Id == AddinsTabId)
                    return tab;

                // На русской локали Civil 3D заголовок может быть «Надстройки»
                var title = tab.Title ?? "";
                if (title.IndexOf("Add", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf("Надстрой", StringComparison.OrdinalIgnoreCase) >= 0)
                    byTitle = tab;
            }

            return byTitle;
        }

        // ── Гарантируем наличие СТАНДАРТНОЙ вкладки «Надстройки» ────────────────
        //    Используем системный Id ID_ADDINSTAB, чтобы это была именно вкладка
        //    «Надстройки» AutoCAD, а не собственная вкладка приложения.
        private static RibbonTab EnsureAddinsTab(RibbonControl ribbon)
        {
            const string AddinsTabId = "ID_ADDINSTAB";
            foreach (RibbonTab t in ribbon.Tabs)
                if (t.Id == AddinsTabId) return t;

            var tab = new RibbonTab
            {
                Title = "Надстройки",
                Id    = AddinsTabId
            };
            ribbon.Tabs.Add(tab);
            return tab;
        }

        // ── Двустрочный заголовок панели ─────────────────────────────────────
        private static int _titleFixRetries = 0;

        private static RibbonControl? _ribbonForFix;

        // Повторно применяем двустрочный заголовок, когда вкладка «Надстройки»
        // становится активной (WPF строит её содержимое только в этот момент).
        private static void OnAddinsTabActivated(
            object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "IsActive" && e.PropertyName != "IsVisible") return;
            if (sender is RibbonTab tab && tab.IsActive && _ribbonForFix != null)
            {
                Log("Add-ins tab activated → re-applying panel title fix");
                SchedulePanelTitleFix(_ribbonForFix);
            }
        }

        private static void SchedulePanelTitleFix(RibbonControl ribbon)
        {
            _titleFixRetries = 0;
            _ribbonForFix = ribbon;
            try
            {
                // Попытка сразу (через BeginInvoke — после текущего цикла WPF)
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(() =>
                    {
                        if (ribbon is System.Windows.DependencyObject dobj && FixTitleInTree(dobj))
                        {
                            Log("Panel title fix: SUCCESS (immediate)");
                            return;
                        }
                        // Не удалось сразу — запускаем таймер с коротким интервалом
                        var timer = new System.Windows.Threading.DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(500)
                        };
                        timer.Tick += OnTitleFixTick;
                        timer.Start();
                    }));
            }
            catch (System.Exception ex) { Log($"SchedulePanelTitleFix exception: {ex.Message}"); }
        }

        private static void OnTitleFixTick(object? sender, EventArgs e)
        {
            try
            {
                var ribbon = _ribbonForFix;
                Log($"TitleFix tick #{_titleFixRetries}, ribbon={ribbon != null}, " +
                    $"ribbon is DependencyObject={ribbon is System.Windows.DependencyObject}");

                if (ribbon is System.Windows.DependencyObject dobj && FixTitleInTree(dobj))
                {
                    Log("Panel title fix: SUCCESS");
                    ((System.Windows.Threading.DispatcherTimer)sender!).Stop();
                    return;
                }

                if (_titleFixRetries++ >= 15)
                {
                    Log("Panel title fix: gave up after 15 retries");
                    ((System.Windows.Threading.DispatcherTimer)sender!).Stop();
                }
            }
            catch (System.Exception ex)
            {
                Log($"Panel title fix exception: {ex.Message}");
                ((System.Windows.Threading.DispatcherTimer)sender!).Stop();
            }
        }

        private static bool FixTitleInTree(System.Windows.DependencyObject obj)
        {
            if (obj == null) return false;
            int count;
            try { count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(obj); }
            catch { return false; }

            for (int i = 0; i < count; i++)
            {
                System.Windows.DependencyObject child;
                try { child = System.Windows.Media.VisualTreeHelper.GetChild(obj, i); }
                catch { continue; }

                if (child is System.Windows.Controls.TextBlock tb
                    && tb.Text != null
                    && tb.Text.IndexOf("Картограмма", StringComparison.Ordinal) >= 0)
                {
                    var vis = tb.Visibility;
                    var parentType = System.Windows.Media.VisualTreeHelper
                        .GetParent(tb)?.GetType().Name ?? "null";
                    Log($"Panel title fix: found TextBlock, Text=\"{tb.Text}\", " +
                        $"Vis={vis}, ActH={tb.ActualHeight}, ActW={tb.ActualWidth}, " +
                        $"parent={parentType}");

                    // Пропускаем скрытые TextBlock (это текст кнопки при ShowText=false)
                    if (vis != System.Windows.Visibility.Visible || tb.ActualHeight < 1)
                    {
                        Log("  → skipped (not visible), continuing search...");
                        // НЕ возвращаем true — ищем дальше
                    }
                    else
                    {
                        // Нашли видимый заголовок панели — модифицируем
                        tb.Text = "Картограмма\nземляных работ";
                        tb.TextWrapping = System.Windows.TextWrapping.Wrap;
                        tb.TextAlignment = System.Windows.TextAlignment.Center;
                        tb.TextTrimming = System.Windows.TextTrimming.None;
                        tb.Height = double.NaN;
                        tb.MaxHeight = double.PositiveInfinity;
                        tb.ClipToBounds = false;

                        // Расширяем PanelTitleBar и StackPanel под две строки
                        var cur = System.Windows.Media.VisualTreeHelper.GetParent(tb);
                        int depth = 0;
                        while (cur != null && depth < 8)
                        {
                            if (cur is System.Windows.FrameworkElement fe)
                            {
                                string typeName = fe.GetType().Name;
                                Log($"  parent[{depth}]: {typeName}, " +
                                    $"H={fe.Height}, MinH={fe.MinHeight}, ActH={fe.ActualHeight}");

                                fe.ClipToBounds = false;

                                // PanelTitleBar и его StackPanel — нужна высота под 2 строки
                                if (typeName == "PanelTitleBar"
                                    || (typeName == "StackPanel" && fe.ActualHeight < 20))
                                {
                                    fe.Height = double.NaN;
                                    fe.MinHeight = 32;
                                }
                            }
                            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
                            depth++;
                        }
                        return true;
                    }
                }

                if (FixTitleInTree(child))
                    return true;
            }
            return false;
        }

        // ════════════════════════════════════════════════════════════════════════
        //  Иконка — картограмма в изометрии (сетка выемка/насыпь, вид сверху-сбоку)
        // ════════════════════════════════════════════════════════════════════════
        private static BitmapSource MakeIcon(int size)
        {
            // Рендерим в 4× пиксельном разрешении и выставляем DPI 4×96=384,
            // чтобы логический размер остался равным `size` DIP. 4× совпадает
            // с максимальным Windows HiDPI-масштабом (400%): иконка чёткая на
            // любом дисплее, а DPI-метаданные PNG остаются в рамках значений,
            // которые корректно обрабатывают ленты AutoCAD 2015-2026.
            // (Экстремальный SCALE=8 → DPI=768 работает только на 2024+.)
            const int SCALE = 4;
            int px = size * SCALE;

            using var bmp = new System.Drawing.Bitmap(px, px,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            bmp.SetResolution(96f * SCALE, 96f * SCALE);
            using var g = System.Drawing.Graphics.FromImage(bmp);

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.Clear(System.Drawing.Color.Transparent);

            float s = px / 32f;

            // ── Изометрические векторы (2:1, столбец вправо-вниз, строка влево-вниз) ─
            float dcx = 3.4f * s, dcy = 1.5f * s;   // шаг по столбцу
            float drx = -2.8f * s, dry = 1.5f * s;   // шаг по строке
            float ox = 13.5f * s, oy = 9.0f * s;   // начало координат

            System.Drawing.PointF Pt(float c, float r) =>
                new(ox + c * dcx + r * drx, oy + c * dcy + r * dry);

            // ── Значения ячеек: -1=глубокая выемка, +1=высокая насыпь ─────────
            // 4 строки × 5 столбцов, переход от выемки (лево-верх) к насыпи (право-низ)
            float[,] v =
            {
                { -0.85f, -0.70f, -0.45f, -0.15f,  0.20f },
                { -0.65f, -0.45f, -0.15f,  0.25f,  0.55f },
                { -0.30f, -0.05f,  0.25f,  0.55f,  0.75f },
                {  0.10f,  0.35f,  0.60f,  0.75f,  0.85f },
            };
            const int COLS = 5, ROWS = 4;

            // Цвет ячейки: выемка — красно-оранжевый, насыпь — сине-голубой
            static System.Drawing.Color CellColor(float val)
            {
                if (val < 0)
                {
                    float t = -val;
                    return System.Drawing.Color.FromArgb(170,
                        (int)(185 + 70 * t), (int)(55 + 20 * (1 - t)), (int)(55 + 20 * (1 - t)));
                }
                else
                {
                    float t = val;
                    return System.Drawing.Color.FromArgb(170,
                        (int)(50 + 25 * (1 - t)), (int)(100 + 30 * (1 - t)), (int)(185 + 70 * t));
                }
            }

            // ── Заливка ячеек (сзади-вперёд: сначала дальние строки) ──────────
            for (int r = 0; r < ROWS; r++)
                for (int c = 0; c < COLS; c++)
                {
                    var pts = new[]
                    {
                    Pt(c,   r),   Pt(c+1, r),
                    Pt(c+1, r+1), Pt(c,   r+1),
                };
                    using var br = new System.Drawing.SolidBrush(CellColor(v[r, c]));
                    g.FillPolygon(br, pts);
                }

            // ── Сетка (внутренние линии) ──────────────────────────────────────
            float lw = Math.Max(0.4f, 0.55f * s);
            using var penInner = new System.Drawing.Pen(
                System.Drawing.Color.FromArgb(180, 255, 255, 255), lw);
            for (int c = 1; c < COLS; c++)
                g.DrawLine(penInner, Pt(c, 0), Pt(c, ROWS));
            for (int r = 1; r < ROWS; r++)
                g.DrawLine(penInner, Pt(0, r), Pt(COLS, r));

            // ── Внешняя рамка (ярче) ──────────────────────────────────────────
            using var penBorder = new System.Drawing.Pen(
                System.Drawing.Color.FromArgb(240, 255, 255, 255), lw * 1.6f);
            g.DrawPolygon(penBorder, new[]
            {
                Pt(0, 0), Pt(COLS, 0), Pt(COLS, ROWS), Pt(0, ROWS),
            });

            // ── Конвертируем с сохранением прозрачности ───────────────────────
            using var ms = new System.IO.MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            var dec = new System.Windows.Media.Imaging.PngBitmapDecoder(
                ms,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            return dec.Frames[0];
        }

        private static RibbonToolTip MakeToolTip(string title, string content) =>
            new RibbonToolTip
            {
                Title = title,
                Content = content,
                IsHelpEnabled = false
            };

        private static void WriteToCmd(string msg)
        {
            try
            {
                Application.DocumentManager.MdiActiveDocument?
                    .Editor.WriteMessage($"\n[Картограмма] {msg}\n");
            }
            catch { /* документ может быть не открыт */ }
        }

        private static readonly string _logFile =
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "KartogrammaPlugin",
                "KartogrammaPlugin_log.txt");

        private static void Log(string msg)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_logFile)!);
                System.IO.File.AppendAllText(_logFile,
                    $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n");
            }
            catch { }
        }

    }

    // ════════════════════════════════════════════════════════════════════════════
    //  ICommand — передаёт команду AutoCAD при клике на кнопку ленты
    // ════════════════════════════════════════════════════════════════════════════
    internal sealed class AcadCommandHandler : ICommand
    {
        private readonly string _cmd;
        public AcadCommandHandler(string cmd) => _cmd = cmd;

        public bool CanExecute(object? parameter) => true;

        // Событие обязательно для ICommand, но нам не нужно
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public void Execute(object? parameter)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            // true = echo команды в командной строке (удобно для отладки)
            doc?.SendStringToExecute(_cmd + "\n", false, false, true);
        }
    }
}
