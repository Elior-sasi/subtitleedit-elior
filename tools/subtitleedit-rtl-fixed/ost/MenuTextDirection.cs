using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;

namespace Nikse.SubtitleEdit.Features.Main.MainHelpers;

internal static class MenuTextDirection
{
    private static readonly AttachedProperty<bool> HebrewLayoutProperty =
        AvaloniaProperty.RegisterAttached<MenuItem, ContentPresenter, bool>("CustomHebrewHeaderLayout");
    private static readonly AttachedProperty<bool> HookedProperty =
        AvaloniaProperty.RegisterAttached<MenuItem, ContentPresenter, bool>("CustomHebrewHeaderHooked");
    // Shared size group of the template's shortcut column (Avalonia Fluent MenuItem template).
    private const string GestureColumnGroup = "MenuItemIGT";
    static MenuTextDirection()
    {
        HebrewLayoutProperty.Changed.AddClassHandler<ContentPresenter>((presenter, _) =>
        {
            if (!presenter.GetValue(HebrewLayoutProperty)) return;
            if (!presenter.GetValue(HookedProperty))
            {
                // Hook once per presenter: style re-application must not add handlers again.
                presenter.SetValue(HookedProperty, true);
                presenter.AttachedToVisualTree += (_, _) => ArrangeHeader(presenter);
                presenter.PropertyChanged += (_, change) =>
                {
                    if (change.Property == ContentPresenter.ContentProperty) ArrangeHeader(presenter);
                };
                if (presenter.TemplatedParent is MenuItem owner)
                    owner.PropertyChanged += (_, change) =>
                    {
                        if (change.Property == Visual.FlowDirectionProperty) ArrangeHeader(presenter);
                    };
            }
            ArrangeHeader(presenter);
        });
    }
    private static void ArrangeHeader(ContentPresenter presenter)
    {
        if (presenter.TemplatedParent is not MenuItem item || presenter.Parent is not Grid grid) return;
        ApplyHeaderFont(item);
        var rtl = RightToLeftHelper.GetContentDirection(item.Header as string ?? (item.Header as TextBlock)?.Text, FlowDirection.LeftToRight) == FlowDirection.RightToLeft;
        // Only a physically left-to-right row needs a Hebrew caption moved to the right edge with
        // its shortcut on the left. A right-to-left row (for example the context menu of a Hebrew
        // text box) is already mirrored by the template; moving the caption there pushed it to the
        // far side, next to the submenu arrow and away from its icon.
        var swap = rtl && item.FlowDirection == FlowDirection.LeftToRight;
        Grid.SetColumn(presenter, swap ? 3 : 2);
        // The shortcut column is shared by all rows of a popup. A moved caption must not widen it
        // for the other rows: with large text their captions were cut off at the popup's maximum
        // width (English captions next to custom Hebrew items in the same menu).
        if (grid.ColumnDefinitions.Count > 3)
            grid.ColumnDefinitions[3].SharedSizeGroup = swap ? null : GestureColumnGroup;
        foreach (var child in grid.Children)
            if (child is TextBlock gesture && gesture.Name == "PART_InputGestureText")
            {
                Grid.SetColumn(gesture, swap ? 2 : 3);
                gesture.FlowDirection = FlowDirection.LeftToRight;
                gesture.HorizontalAlignment = swap ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Right;
                gesture.Margin = swap ? new Thickness(0, 0, 24, 0) : new Thickness(24, 0, 0, 0);
            }
    }

    internal static void ApplyHeaderFont(MenuItem item)
    {
        // Custom TextBlock headers must follow the native menu font, including UI scale changes.
        // Dialog labels can keep their own readable size; this is scoped to menu presenters.
        if (item.Header is TextBlock header)
            header.Bind(TextBlock.FontSizeProperty, new Binding(nameof(MenuItem.FontSize))
            {
                Source = item,
                Mode = BindingMode.OneWay,
            });
    }

    internal static void AddStyles(Styles styles)
    {
        // Scope direction to the header presenter. The menu row, icons, shortcut
        // column and main editor keep their existing physical layout.
        styles.Add(new Style(x => x.OfType<MenuItem>().Template().OfType<ContentPresenter>().Name("PART_HeaderPresenter"))
        {
            Setters =
            {
                new Setter(HebrewLayoutProperty, true),
                new Setter(ContentPresenter.FlowDirectionProperty, new Binding(nameof(MenuItem.Header))
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
                    Mode = BindingMode.OneWay,
                    Converter = HeaderDirectionConverter.Instance,
                }),
            },
        });
        styles.Add(new Style(x => x.OfType<MenuItem>().Template().OfType<TextBlock>().Name("PART_InputGestureText"))
        {
            Setters = { new Setter(TextBlock.FlowDirectionProperty, FlowDirection.LeftToRight) },
        });
        // The menu bar wraps to a second row instead of pushing menus past the window edge, so
        // every top-level menu stays reachable with large text, UI scale or Windows scaling.
        styles.Add(new Style(x => x.OfType<Menu>())
        {
            Setters =
            {
                new Setter(ItemsControl.ItemsPanelProperty, new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal })),
                new Setter(Avalonia.Layout.Layoutable.HeightProperty, double.NaN),
            },
        });
    }

    private sealed class HeaderColumnConverter : IValueConverter
    {
        private readonly bool _header;
        internal HeaderColumnConverter(bool header) => _header = header;
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var rtl = RightToLeftHelper.GetContentDirection(value as string ?? (value as TextBlock)?.Text, FlowDirection.LeftToRight) == FlowDirection.RightToLeft;
            return _header ? (rtl ? 3 : 2) : (rtl ? 2 : 3);
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    private sealed class HeaderDirectionConverter : IValueConverter
    {
        internal static readonly HeaderDirectionConverter Instance = new();
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => RightToLeftHelper.GetContentDirection(value as string ?? (value as TextBlock)?.Text, FlowDirection.LeftToRight);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

