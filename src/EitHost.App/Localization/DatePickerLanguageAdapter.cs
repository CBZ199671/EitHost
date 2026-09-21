using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Automation;

namespace EitHost.App.Localization;

/// <summary>Localizes the native calendar without writing back formatted date strings.</summary>
internal sealed class DatePickerLanguageAdapter : IDisposable
{
    private readonly DatePicker picker;
    private readonly Func<UiLanguage> language;

    internal DatePickerLanguageAdapter(DatePicker picker, Func<UiLanguage> language)
    {
        this.picker = picker;
        this.language = language;
        picker.Loaded += OnRefresh;
        picker.CalendarOpened += OnRefresh;
        picker.SelectedDateChanged += OnRefresh;
    }

    internal void Apply() => Apply(picker, language());

    private void OnRefresh(object? sender, RoutedEventArgs e) => Apply();

    internal static void Apply(DatePicker picker, UiLanguage language)
    {
        var english = language == UiLanguage.English;
        var xmlLanguage = XmlLanguage.GetLanguage(language.ToCulture().Name);
        picker.SetCurrentValue(FrameworkElement.LanguageProperty, xmlLanguage);
        picker.ApplyTemplate();

        if (picker.Template?.FindName("PART_TextBox", picker) is DatePickerTextBox textBox)
        {
            textBox.ApplyTemplate();
            if (textBox.Template?.FindName("PART_Watermark", textBox) is ContentControl watermark)
            {
                watermark.SetCurrentValue(ContentControl.ContentProperty, english ? "Select a date" : "选择日期");
            }
        }

        if (picker.Template?.FindName("PART_Button", picker) is Button button)
        {
            button.SetCurrentValue(ContentControl.ContentProperty, english ? "Open calendar" : "打开日历");
            AutomationProperties.SetName(button, english ? "Open calendar" : "打开日历");
        }

        if (WindowLanguageController.FindTemplatePopup(picker)?.Child is not FrameworkElement popupRoot)
        {
            return;
        }

        popupRoot.SetCurrentValue(FrameworkElement.LanguageProperty, xmlLanguage);
        foreach (var calendar in Descendants(popupRoot).OfType<Calendar>())
        {
            var changed = !Equals(calendar.ReadLocalValue(FrameworkElement.LanguageProperty), xmlLanguage);
            calendar.SetCurrentValue(FrameworkElement.LanguageProperty, xmlLanguage);
            calendar.ApplyTemplate();
            if (changed && calendar.Template is { } template)
            {
                // WPF skips its cell refresh on Language changes when FirstDayOfWeek
                // is bound (as it always is inside DatePicker). Rebuild the visual
                // template only; selection, display date and bindings remain intact.
                calendar.SetCurrentValue(Control.TemplateProperty, null);
                calendar.SetCurrentValue(Control.TemplateProperty, template);
                calendar.ApplyTemplate();
            }
            foreach (var item in Descendants(calendar).OfType<CalendarItem>())
            {
                item.ApplyTemplate();
                SetNavigation(item, "PART_PreviousButton", english ? "Previous" : "上一个");
                SetNavigation(item, "PART_NextButton", english ? "Next" : "下一个");
            }
        }
    }

    private static void SetNavigation(CalendarItem item, string name, string text)
    {
        if (item.Template?.FindName(name, item) is Button button)
        {
            button.SetCurrentValue(ContentControl.ContentProperty, text);
            AutomationProperties.SetName(button, text);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }

    public void Dispose()
    {
        picker.Loaded -= OnRefresh;
        picker.CalendarOpened -= OnRefresh;
        picker.SelectedDateChanged -= OnRefresh;
    }
}
