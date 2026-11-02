#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ShareX;

// What the tray panel shows, grouped by how much room each action gets.
internal sealed record TrayPanelModel(
    IReadOnlyList<TrayPanelHero> Heroes,
    IReadOnlyList<MainMenuEntry> Tiles,
    IReadOnlyList<MainMenuEntry> Sections,
    IReadOnlyList<MainMenuEntry> Footer,
    IReadOnlyList<MainMenuEntry> Header);

internal sealed record TrayPanelHero(MainMenuEntry Entry, string Caption, string StyleClass);

// The panel that opens from the tray icon: the two things people come for get large buttons, the
// everyday captures and uploads get tiles, and everything else stays one click away in menus.
internal partial class TrayPanelWindow : Window
{
    private readonly Func<IEnumerable<MainMenuEntry>, ContextMenu> _buildMenu;
    private ContextMenu? _openMenu;
    private bool _closing;
    private PixelRect? _trayBounds;

    public TrayPanelWindow()
        : this(new TrayPanelModel([], [], [], [], []), _ => new ContextMenu())
    {
    }

    public TrayPanelWindow(TrayPanelModel model, Func<IEnumerable<MainMenuEntry>, ContextMenu> buildMenu)
    {
        _buildMenu = buildMenu;
        AvaloniaXamlLoader.Load(this);
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();

        Panel headerButtons = this.FindControl<StackPanel>("HeaderButtons")!;
        Panel heroGrid = this.FindControl<UniformGrid>("HeroGrid")!;
        Panel tileGrid = this.FindControl<UniformGrid>("TileGrid")!;
        Panel sectionGrid = this.FindControl<UniformGrid>("SectionGrid")!;
        Panel footerGrid = this.FindControl<UniformGrid>("FooterGrid")!;

        foreach (MainMenuEntry entry in Visible(model.Header)) headerButtons.Children.Add(CreateRoundButton(entry));
        foreach (TrayPanelHero hero in model.Heroes.Where(h => h.Entry.IsVisible)) heroGrid.Children.Add(CreateHero(hero));
        foreach (MainMenuEntry entry in Visible(model.Tiles)) tileGrid.Children.Add(CreateTile(entry));
        foreach (MainMenuEntry entry in Visible(model.Sections)) sectionGrid.Children.Add(CreateSection(entry));
        foreach (MainMenuEntry entry in Visible(model.Footer)) footerGrid.Children.Add(CreateRoundButton(entry));

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) ClosePanel();
        };
        Deactivated += (_, _) =>
        {
            // Opening one of the panel's own menus must not dismiss the panel underneath it.
            if (_openMenu == null) ClosePanel();
        };
        SizeChanged += (_, _) =>
        {
            if (IsVisible && !_closing) PositionAtTray();
        };
    }

    private static IEnumerable<MainMenuEntry> Visible(IEnumerable<MainMenuEntry> entries) =>
        entries.Where(e => e.IsVisible && !e.IsSeparator);

    public void ShowAtTray(PixelRect? trayBounds)
    {
        _trayBounds = trayBounds;
        // Pick the correct monitor before showing, then place again after content and DPI settle.
        Screen? screen = GetTrayScreen();
        if (screen != null)
        {
            Position = new PixelPoint(screen.WorkingArea.Right - 1, screen.WorkingArea.Bottom - 1);
            MaxWidth = Math.Max(1, screen.WorkingArea.Width / screen.Scaling - 16);
            MaxHeight = Math.Max(1, screen.WorkingArea.Height / screen.Scaling - 16);
        }

        Opacity = 0;
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_closing) return;
            PositionAtTray();
            Opacity = 1;
        }, DispatcherPriority.Loaded);

        Show();
        Activate();
    }

    private Screen? GetTrayScreen() => _trayBounds is { } anchor
        ? Screens.ScreenFromPoint(new PixelPoint(anchor.X + anchor.Width / 2, anchor.Y + anchor.Height / 2)) ?? Screens.Primary
        : Screens.Primary;

    private void PositionAtTray()
    {
        Screen? screen = GetTrayScreen();
        if (screen == null) return;
        PixelRect anchor = _trayBounds ?? new PixelRect(screen.Bounds.Right - 1, screen.Bounds.Bottom - 1, 1, 1);
        PixelSize size = new((int)Math.Ceiling(Bounds.Width * screen.Scaling), (int)Math.Ceiling(Bounds.Height * screen.Scaling));
        Position = CalculateTrayPosition(anchor, screen.Bounds, screen.WorkingArea, size, screen.Scaling);
    }

    internal static PixelPoint CalculateTrayPosition(PixelRect anchor, PixelRect screen, PixelRect area, PixelSize size, double scaling)
    {
        int gap = (int)Math.Ceiling(8 * scaling);
        int x = anchor.Right - size.Width;
        int y = area.Bottom - size.Height - gap;
        int left = area.X - screen.X;
        int top = area.Y - screen.Y;
        int right = screen.Right - area.Right;
        int bottom = screen.Bottom - area.Bottom;

        // Work-area insets identify the taskbar edge, including vertical and top taskbars.
        // Auto-hide taskbars have no inset; use the icon's nearest screen edge in that case.
        int edge = 0; // bottom, top, left, right
        int largest = Math.Max(Math.Max(left, right), Math.Max(top, bottom));
        if (largest > 0)
        {
            if (bottom == largest) edge = 0;
            else if (top == largest) edge = 1;
            else if (left == largest) edge = 2;
            else edge = 3;
        }
        else
        {
            int centerX = anchor.X + anchor.Width / 2;
            int centerY = anchor.Y + anchor.Height / 2;
            int[] distances = [Math.Abs(screen.Bottom - centerY), Math.Abs(centerY - screen.Y),
                Math.Abs(centerX - screen.X), Math.Abs(screen.Right - centerX)];
            edge = Array.IndexOf(distances, distances.Min());
        }

        if (edge == 1) y = area.Y + gap;
        else if (edge == 2 || edge == 3)
        {
            x = edge == 2 ? area.X + gap : area.Right - size.Width - gap;
            y = anchor.Bottom - size.Height;
        }

        return new PixelPoint(
            Math.Clamp(x, area.X + gap, Math.Max(area.X + gap, area.Right - size.Width - gap)),
            Math.Clamp(y, area.Y + gap, Math.Max(area.Y + gap, area.Bottom - size.Height - gap)));
    }

    public void ClosePanel()
    {
        if (_closing) return;
        _closing = true;
        _openMenu?.Close();
        Close();
    }

    private Button CreateHero(TrayPanelHero hero)
    {
        Grid content = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        TextBlock icon = Icon(hero.Entry.Icon, 26);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        StackPanel text = new()
        {
            Children =
            {
                new TextBlock { Text = TrimEllipsis(hero.Entry.Header), FontSize = 15, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = hero.Caption, FontSize = 11.5, FontWeight = FontWeight.Normal, Opacity = 0.85, TextWrapping = TextWrapping.Wrap, MaxLines = 2 }
            }
        };
        Grid.SetRow(icon, 0);
        Grid.SetRow(text, 2);
        content.Children.Add(icon);
        content.Children.Add(text);

        Button button = new() { Content = content };
        button.Classes.Add("hero");
        button.Classes.Add(hero.StyleClass);
        button.Click += (_, _) => Run(hero.Entry, button);
        return button;
    }

    private Button CreateTile(MainMenuEntry entry)
    {
        TextBlock icon = Icon(entry.Icon, 20);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.Foreground = (IBrush?)this.FindResource("ShareX.Brush.Accent") ?? Brushes.DodgerBlue;
        TextBlock label = new() { Text = TrimEllipsis(entry.Header) };
        label.Classes.Add("tile-label");

        Button button = new() { Content = new StackPanel { Spacing = 7, VerticalAlignment = VerticalAlignment.Center, Children = { icon, label } } };
        button.Classes.Add("tile");
        button.Click += (_, _) => Run(entry, button);
        return button;
    }

    private Button CreateSection(MainMenuEntry entry)
    {
        TextBlock icon = Icon(entry.Icon, 15);
        TextBlock label = new() { Text = TrimEllipsis(entry.Header), VerticalAlignment = VerticalAlignment.Center };
        label.Classes.Add("section-label");
        TextBlock chevron = Icon(LucideIcons.chevron_right, 14);
        chevron.Opacity = 0.5;

        Grid content = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(chevron, 2);
        content.Children.Add(icon);
        content.Children.Add(label);
        content.Children.Add(chevron);

        Button button = new() { Content = content };
        button.Classes.Add("section");
        button.Click += (_, _) => Run(entry, button);
        return button;
    }

    private Button CreateRoundButton(MainMenuEntry entry)
    {
        Button button = new() { Content = Icon(entry.Icon, 16), HorizontalAlignment = HorizontalAlignment.Center };
        button.Classes.Add("round");
        ToolTip.SetTip(button, TrimEllipsis(entry.Header));
        button.Click += (_, _) => Run(entry, button);
        return button;
    }

    private static TextBlock Icon(string glyph, double size)
    {
        TextBlock icon = new() { Text = glyph, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        icon.Classes.Add("icon");
        return icon;
    }

    private static string TrimEllipsis(string header) => header.TrimEnd('.', '…');

    private void Run(MainMenuEntry entry, Control source)
    {
        if (entry.CreateChildren != null)
        {
            OpenMenu(entry.CreateChildren(), source);
            return;
        }

        Func<System.Threading.Tasks.Task>? execute = entry.ExecuteAsync;
        ClosePanel();

        if (execute != null)
        {
            // Let the panel disappear first so it is never part of a capture.
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await execute();
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                }
            }, DispatcherPriority.Background);
        }
    }

    private void OpenMenu(IReadOnlyList<MainMenuEntry> entries, Control source)
    {
        _openMenu?.Close();

        ContextMenu menu = _buildMenu(entries);
        _openMenu = menu;
        menu.Placement = PlacementMode.LeftEdgeAlignedTop;

        // Make the panel invisible the instant an item is chosen, so a capture started from a menu
        // can never include it.
        menu.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.Source is Visual visual && visual.FindAncestorOfType<MenuItem>(true) is { IsEnabled: true, HasSubMenu: false, StaysOpenOnClick: false })
            {
                Opacity = 0;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        menu.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_openMenu, menu)) return;
            _openMenu = null;

            // Dismissing a menu by coming back to the panel keeps it open; anything else is done with it.
            if (Opacity == 0 || !IsPointerOver) ClosePanel();
        };
        menu.Open(source);
    }
}
