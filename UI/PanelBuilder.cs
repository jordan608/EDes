// ═══════════════════════════════════════════════════════════════════════════
//  PanelBuilder.cs — Reusable settings-panel widgets
//
//  The engine's settings tabs AND each game's settings tab build their UI with
//  the same helpers, so everything looks and behaves consistently:
//    • Root()/Wrap()         — the scrollable panel container
//    • AddSection()          — collapsible accordion section (mutually exclusive)
//    • AddSlider()           — slider + editable, numeric-validated value box that
//                              commits on Enter / blur (not per keystroke)
//    • AddToggle()/AddRgb()/AddButton()/AddInfo()
//
//  `onChanged` is invoked after any edit (the host wires this to its debounced
//  save), so PanelBuilder has no dependency on a specific settings object.
// ═══════════════════════════════════════════════════════════════════════════

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;

namespace EDes.UI
{
    public sealed class PanelBuilder
    {
        private readonly Action _onChanged;

        public PanelBuilder(Action onChanged) { _onChanged = onChanged; }

        // ── Containers ─────────────────────────────────────────────────────────
        public StackPanel Root() =>
            new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 8) };

        public ScrollViewer Wrap(StackPanel p) =>
            new ScrollViewer
            {
                Content = p,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            };

        // ── AddSection — collapsible accordion section ───────────────────────
        // Returns the content StackPanel to fill. Expanding one section collapses
        // the others in the same group, keeping the panel compact.
        public StackPanel AddSection(StackPanel root, string title,
                                     List<Expander> group, bool expanded = false)
        {
            var content = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 0, 2) };
            var exp = new Expander
            {
                Header              = title,
                Content             = content,
                IsExpanded          = expanded,
                Margin              = new Thickness(6, 2, 6, 0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            };
            exp.PropertyChanged += (_, e) =>
            {
                if (e.Property != Expander.IsExpandedProperty || !exp.IsExpanded) return;
                foreach (var other in group)
                    if (!ReferenceEquals(other, exp)) other.IsExpanded = false;
            };
            group.Add(exp);
            root.Children.Add(exp);
            return content;
        }

        // ── AddHeader — a plain, non-collapsible group label ──────────────────
        // For panels shown as one flat list. An Expander would let the reader hide a
        // setting and then not know it exists; a label cannot.
        public StackPanel AddHeader(StackPanel root, string title)
        {
            root.Children.Add(new TextBlock
            {
                Text       = title.ToUpperInvariant(),
                FontSize   = 10,
                FontWeight = FontWeight.Bold,
                Opacity    = 0.55,
                Margin     = new Thickness(10, 12, 10, 3),
            });
            var content = new StackPanel { Spacing = 2 };
            root.Children.Add(content);
            return content;
        }

        // ── AddInfo — dim informational text ──────────────────────────────────
        public void AddInfo(StackPanel p, string text)
        {
            p.Children.Add(new TextBlock
            {
                Text         = text,
                FontSize     = 10,
                Opacity      = 0.50,
                Margin       = new Thickness(10, 1, 10, 1),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // ── AddButton — full-width action button ──────────────────────────────
        public void AddButton(StackPanel p, string label, Action onClick)
        {
            var btn = new Button
            {
                Content             = label,
                FontSize            = 11,
                Padding             = new Thickness(6, 4),
                Margin              = new Thickness(10, 4, 10, 2),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            };
            btn.Click += (_, _) => onClick();
            p.Children.Add(btn);
        }

        // ── AddSlider — slider + editable, validated value box ────────────────
        // Only accepts numeric characters; the value is NOT applied until the box
        // loses focus or Enter is pressed. Invalid/out-of-range text reverts on
        // commit. onChange fires live while dragging the slider.
        public void AddSlider(StackPanel p, string label,
                              double min, double max, double initial,
                              Action<double> onChange, string fmt = "F2")
        {
            bool allowDecimal = fmt != "F0";
            bool allowNeg     = min < 0;

            var valueBox = new TextBox
            {
                Text          = initial.ToString(fmt),
                FontSize      = 10,
                Width         = 58,
                Padding       = new Thickness(4, 2),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            RestrictNumeric(valueBox, allowNeg, allowDecimal);

            var slider = new Slider
            {
                Minimum       = min,
                Maximum       = max,
                Value         = initial,
                TickFrequency = (max - min) / 200.0,
                Margin        = new Thickness(0, 0, 4, 0),
            };
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property.Name != nameof(Slider.Value)) return;
                onChange(slider.Value);
                _onChanged();
                if (!valueBox.IsFocused) valueBox.Text = slider.Value.ToString(fmt);
            };

            void Commit()
            {
                if (double.TryParse(valueBox.Text, out double v))
                    slider.Value = Math.Clamp(v, min, max);
                valueBox.Text = slider.Value.ToString(fmt);
            }
            valueBox.KeyDown   += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            valueBox.LostFocus += (_, _) => Commit();

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,58") };
            row.Children.Add(slider);
            row.Children.Add(valueBox);
            Grid.SetColumn(valueBox, 1);

            p.Children.Add(new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(10, 4, 10, 0) });
            p.Children.Add(new Border    { Margin = new Thickness(10, 0, 10, 2), Child = row });
        }

        // ── AddNumber — a validated numeric box with NO slider ────────────────
        // Same validation and commit behaviour as AddSlider's box (filtered input,
        // commit on Enter or blur, revert on nonsense) but without the track. A slider
        // is for exploring a range; these are values you already know and want to type,
        // where dragging to 150000 voxels or 0.08 dead-zone is the slow way round.
        public void AddNumber(StackPanel p, string label, double initial,
                              Action<double> onChange, string fmt = "F2")
        {
            bool allowDecimal = fmt != "F0";

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,92"),
                Margin            = new Thickness(10, 2, 10, 2),
            };

            var caption = new TextBlock
            {
                Text              = label,
                FontSize          = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            Grid.SetColumn(caption, 0);

            var box = new TextBox
            {
                Text                       = initial.ToString(fmt),
                FontSize                   = 11,
                Padding                    = new Thickness(4, 2),
                MinHeight                  = 0,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            };
            Grid.SetColumn(box, 1);
            RestrictNumeric(box, allowNeg: true, allowDecimal);

            double last = initial;

            void Commit()
            {
                if (double.TryParse(box.Text, out double v))
                {
                    last = v;
                    onChange(v);
                    _onChanged();
                }
                // Unparseable input reverts rather than silently applying a zero — a
                // half-typed "-" or "." must not become a value.
                box.Text = last.ToString(fmt);
            }

            box.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter) Commit();
            };
            box.LostFocus += (_, _) => Commit();

            row.Children.Add(caption);
            row.Children.Add(box);
            p.Children.Add(row);
        }

        // ── AddTextBox — free-text row (paths, port names) ────────────────────
        // Commits on Enter or focus loss, never per keystroke. Game input is already
        // suspended while any TextBox has focus (MainWindow wires GotFocus), so typing
        // a path can never drive the simulator.
        public void AddTextBox(StackPanel p, string label, string initial, Action<string> onCommit)
        {
            var box = new TextBox
            {
                Text     = initial,
                FontSize = 10,
                Padding  = new Thickness(4, 2),
                Margin   = new Thickness(10, 0, 10, 2),
            };

            void Commit() { onCommit(box.Text ?? ""); _onChanged(); }
            box.KeyDown   += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            box.LostFocus += (_, _) => Commit();

            p.Children.Add(new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(10, 4, 10, 0) });
            p.Children.Add(box);
        }

        // ── AddLiveInfo — dim text that refreshes itself ──────────────────────
        // For readouts the game thread owns (board stats, scope status): the panel is
        // built once, so a static AddInfo would show whatever was true at build time.
        // Polled at 1 Hz on the UI thread; the supplied func must only read volatile
        // or atomically-assigned state.
        public void AddLiveInfo(StackPanel p, Func<string> text, double intervalSeconds = 1.0)
        {
            var tb = new TextBlock
            {
                Text         = text(),
                FontSize     = 10,
                Opacity      = 0.65,
                Margin       = new Thickness(10, 1, 10, 1),
                TextWrapping = TextWrapping.Wrap,
                FontFamily   = new FontFamily("Consolas,Menlo,monospace"),
            };
            var timer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(intervalSeconds),
            };
            timer.Tick += (_, _) =>
            {
                if (tb.GetVisualRoot() == null) { timer.Stop(); return; }   // panel replaced
                tb.Text = text();
            };
            timer.Start();
            p.Children.Add(tb);
        }

        // ── AddToggle — checkbox with label ───────────────────────────────────
        public void AddToggle(StackPanel p, string label, bool initial, Action<bool> onChange)
        {
            var cb = new CheckBox
            {
                Content   = label,
                IsChecked = initial,
                FontSize  = 11,
                Margin    = new Thickness(10, 2, 10, 2),
            };
            cb.IsCheckedChanged += (_, _) =>
            {
                onChange(cb.IsChecked == true);
                _onChanged();
            };
            p.Children.Add(cb);
        }

        // Convenience overload for int 0/1 fields.
        public void AddIntToggle(StackPanel p, string label, bool initial, Action<bool> onChange)
            => AddToggle(p, label, initial, onChange);

        // ── AddRgb — three 0-255 channel sliders for a packed 0xRRGGBB colour ─
        // getColor reads the live colour each change so the other two channels are
        // preserved; setColor receives the new packed colour to store.
        public void AddRgb(StackPanel p, Func<int> getColor, Action<int> setColor)
        {
            int c0 = getColor();
            AddSlider(p, "R", 0, 255, (c0 >> 16) & 0xFF, v => setColor((getColor() & 0x00FFFF) | ((int)v << 16)), "F0");
            AddSlider(p, "G", 0, 255, (c0 >>  8) & 0xFF, v => setColor((getColor() & 0xFF00FF) | ((int)v <<  8)), "F0");
            AddSlider(p, "B", 0, 255,  c0        & 0xFF, v => setColor((getColor() & 0xFFFF00) |  (int)v),        "F0");
        }

        // ── RestrictNumeric — block non-numeric characters as they're typed ───
        private static void RestrictNumeric(TextBox box, bool allowNeg, bool allowDecimal)
        {
            box.AddHandler(InputElement.TextInputEvent, (s, e) =>
            {
                foreach (char c in e.Text ?? "")
                {
                    bool ok = char.IsDigit(c)
                              || (allowDecimal && c == '.')
                              || (allowNeg && c == '-');
                    if (!ok) { e.Handled = true; return; }
                }
            }, RoutingStrategies.Tunnel);
        }
    }
}
