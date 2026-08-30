using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace EitHost.App.Localization;

internal sealed class WindowLanguageController : IDisposable
{
    private readonly FrameworkElement root;
    private readonly RoutedEventHandler loadedHandler;
    private readonly ContextMenuEventHandler contextMenuOpeningHandler;
    private readonly Dictionary<DependencyObject, ElementRegistration> registrations =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<UIElement> externalScopes = new(ReferenceEqualityComparer.Instance);
    private readonly List<PropertyRegistration> pendingApplications = [];
    private int attachDepth;
    private bool applying;
    private bool disposed;

    internal WindowLanguageController(FrameworkElement root)
    {
        this.root = root ?? throw new ArgumentNullException(nameof(root));
        loadedHandler = OnDescendantLoaded;
        contextMenuOpeningHandler = OnContextMenuOpening;
        root.AddHandler(FrameworkElement.LoadedEvent, loadedHandler, handledEventsToo: true);
        root.AddHandler(ContextMenuService.ContextMenuOpeningEvent, contextMenuOpeningHandler, handledEventsToo: true);
        AttachSubtree(root);
    }

    internal UiLanguage CurrentLanguage { get; private set; } = UiLanguage.SimplifiedChinese;

    internal CultureInfo CurrentCulture => CurrentLanguage.ToCulture();

    internal void SetLanguage(UiLanguage language)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        AttachSubtree(root);
        if (CurrentLanguage == language)
        {
            return;
        }

        CurrentLanguage = language;
        ApplyAll();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        root.RemoveHandler(FrameworkElement.LoadedEvent, loadedHandler);
        root.RemoveHandler(ContextMenuService.ContextMenuOpeningEvent, contextMenuOpeningHandler);
        foreach (var scope in externalScopes)
        {
            scope.RemoveHandler(FrameworkElement.LoadedEvent, loadedHandler);
            scope.RemoveHandler(ContextMenuService.ContextMenuOpeningEvent, contextMenuOpeningHandler);
        }

        externalScopes.Clear();
        foreach (var registration in registrations.Values)
        {
            registration.Dispose();
        }

        registrations.Clear();
    }

    private void OnDescendantLoaded(object sender, RoutedEventArgs e)
    {
        if (disposed || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        AttachSubtree(source);
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (disposed || e.OriginalSource is not FrameworkElement source || source.ContextMenu is null)
        {
            return;
        }

        AttachExternalSubtree(source.ContextMenu);
    }

    private void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (disposed || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (source.GetValue(ToolTipService.ToolTipProperty) is DependencyObject toolTip)
        {
            AttachExternalSubtree(toolTip);
        }
    }

    private void AttachExternalSubtree(DependencyObject subtreeRoot)
    {
        if (subtreeRoot is UIElement scope && externalScopes.Add(scope))
        {
            scope.AddHandler(FrameworkElement.LoadedEvent, loadedHandler, handledEventsToo: true);
            scope.AddHandler(ContextMenuService.ContextMenuOpeningEvent, contextMenuOpeningHandler, handledEventsToo: true);
        }

        AttachSubtree(subtreeRoot);
    }

    // Registration and translation are two passes on purpose: a template part
    // mirrors the value of the element it was generated for, so translating the
    // owner while the walk is still running would let the part record the
    // translation as its own source and strand it there on the way back.
    private void AttachSubtree(DependencyObject subtreeRoot)
    {
        attachDepth++;
        try
        {
            var pending = new Stack<DependencyObject>();
            var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
            pending.Push(subtreeRoot);

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!visited.Add(current))
                {
                    continue;
                }

                AttachElement(current);

                try
                {
                    var visualChildren = VisualTreeHelper.GetChildrenCount(current);
                    for (var index = 0; index < visualChildren; index++)
                    {
                        pending.Push(VisualTreeHelper.GetChild(current, index));
                    }
                }
                catch (InvalidOperationException)
                {
                    // Content elements do not participate in the visual tree.
                }

                foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
                {
                    pending.Push(child);
                }
            }
        }
        finally
        {
            attachDepth--;
        }

        FlushPendingApplications();
    }

    private void FlushPendingApplications()
    {
        if (attachDepth > 0 || pendingApplications.Count == 0)
        {
            return;
        }

        var scheduled = pendingApplications.ToArray();
        pendingApplications.Clear();
        foreach (var registration in scheduled)
        {
            Apply(registration);
        }
    }

    private void AttachElement(DependencyObject element)
    {
        if (registrations.ContainsKey(element))
        {
            return;
        }

        var registration = new ElementRegistration(element);
        registrations.Add(element, registration);

        switch (element)
        {
            case Window:
                AttachProperty(registration, Window.TitleProperty);
                break;
            case TextBlock:
                AttachProperty(registration, TextBlock.TextProperty);
                break;
            case AccessText:
                AttachProperty(registration, AccessText.TextProperty);
                break;
            case Run:
                AttachProperty(registration, Run.TextProperty);
                break;
            case DatePickerTextBox:
                AttachProperty(registration, TextBox.TextProperty);
                break;
            case TextBox textBox when textBox.IsReadOnly:
                AttachProperty(registration, TextBox.TextProperty);
                break;
            case ContentPresenter:
                AttachProperty(registration, ContentPresenter.ContentProperty);
                break;
        }

        if (element is HeaderedContentControl)
        {
            AttachProperty(registration, HeaderedContentControl.HeaderProperty);
        }

        if (element is HeaderedItemsControl)
        {
            AttachProperty(registration, HeaderedItemsControl.HeaderProperty);
        }

        if (element is ContentControl)
        {
            AttachProperty(registration, ContentControl.ContentProperty);
        }

        if (element is FrameworkElement or FrameworkContentElement)
        {
            AttachProperty(registration, ToolTipService.ToolTipProperty);
        }

        if (element is FrameworkElement frameworkElement)
        {
            ToolTipEventHandler toolTipOpening = OnToolTipOpening;
            frameworkElement.AddHandler(
                ToolTipService.ToolTipOpeningEvent,
                toolTipOpening,
                handledEventsToo: true);
            registration.DetachActions.Add(() =>
                frameworkElement.RemoveHandler(ToolTipService.ToolTipOpeningEvent, toolTipOpening));
        }

        if (element is ItemsControl itemsControl)
        {
            EventHandler generatorStatusChanged = (_, _) => AttachGeneratedItemContainers(itemsControl);
            itemsControl.ItemContainerGenerator.StatusChanged += generatorStatusChanged;
            registration.DetachActions.Add(() =>
                itemsControl.ItemContainerGenerator.StatusChanged -= generatorStatusChanged);
            AttachGeneratedItemContainers(itemsControl);
        }

        switch (element)
        {
            case ComboBox comboBox:
                EventHandler dropDownOpened = (_, _) => ScheduleTemplatePopupAttach(comboBox);
                SelectionChangedEventHandler selectionChanged = (_, _) => ApplyComboBoxSelection(comboBox);
                comboBox.DropDownOpened += dropDownOpened;
                comboBox.SelectionChanged += selectionChanged;
                registration.DetachActions.Add(() => comboBox.DropDownOpened -= dropDownOpened);
                registration.DetachActions.Add(() => comboBox.SelectionChanged -= selectionChanged);
                ApplyComboBoxSelection(comboBox);
                if (comboBox.IsDropDownOpen)
                {
                    ScheduleTemplatePopupAttach(comboBox);
                }

                break;
            case MenuItem menuItem:
                RoutedEventHandler submenuOpened = (_, _) => ScheduleTemplatePopupAttach(menuItem);
                menuItem.SubmenuOpened += submenuOpened;
                registration.DetachActions.Add(() => menuItem.SubmenuOpened -= submenuOpened);
                if (menuItem.IsSubmenuOpen)
                {
                    ScheduleTemplatePopupAttach(menuItem);
                }

                break;
        }
    }

    private void AttachGeneratedItemContainers(ItemsControl itemsControl)
    {
        if (disposed || itemsControl.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
        {
            return;
        }

        for (var index = 0; index < itemsControl.Items.Count; index++)
        {
            if (itemsControl.ItemContainerGenerator.ContainerFromIndex(index) is not DependencyObject container)
            {
                continue;
            }

            if (container is FrameworkElement frameworkElement)
            {
                frameworkElement.ApplyTemplate();
            }

            AttachSubtree(container);
        }
    }

    private void ScheduleTemplatePopupAttach(Control owner)
    {
        root.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (disposed)
                {
                    return;
                }

                owner.ApplyTemplate();
                if (FindTemplatePopup(owner) is { Child: { } child })
                {
                    AttachExternalSubtree(child);
                }
            }));
    }

    internal static Popup? FindTemplatePopup(Control owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.ApplyTemplate();
        if (owner.Template?.FindName("PART_Popup", owner) is Popup standardPopup)
        {
            return standardPopup;
        }

        if (owner.Template?.FindName("Popup", owner) is Popup namedPopup)
        {
            return namedPopup;
        }

        var pending = new Stack<DependencyObject>();
        pending.Push(owner);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is Popup popup)
            {
                return popup;
            }

            try
            {
                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                {
                    pending.Push(VisualTreeHelper.GetChild(current, index));
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        return null;
    }

    private void AttachProperty(ElementRegistration element, DependencyProperty property)
    {
        if (element.Properties.Any(item => item.Property == property))
        {
            return;
        }

        var descriptor = DependencyPropertyDescriptor.FromProperty(property, element.Owner.GetType());
        if (descriptor is null)
        {
            return;
        }

        var initial = element.Owner.GetValue(property) as string;
        var propertyRegistration = new PropertyRegistration(
            element.Owner,
            property,
            descriptor,
            ResolveSourceText(element.Owner, initial),
            OnTargetPropertyChanged);
        element.Properties.Add(propertyRegistration);
        propertyRegistration.Attach();
        if (attachDepth > 0)
        {
            pendingApplications.Add(propertyRegistration);
            return;
        }

        Apply(propertyRegistration);
    }

    // A template part realized after its owner was already translated starts out
    // holding English. Recording that as the source would pin it to English for
    // good, so the owner's own untranslated text is adopted instead.
    private string? ResolveSourceText(DependencyObject owner, string? current)
    {
        if (current is null
            || CurrentLanguage == UiLanguage.SimplifiedChinese
            || EnglishUiText.ContainsChinese(current))
        {
            return current;
        }

        var ancestor = GetLayoutParent(owner);
        for (var depth = 0; depth < 6 && ancestor is not null; depth++)
        {
            if (registrations.TryGetValue(ancestor, out var registration))
            {
                foreach (var property in registration.Properties)
                {
                    if (property.SourceText is { } source
                        && EnglishUiText.ContainsChinese(source)
                        && string.Equals(EnglishUiText.Translate(source), current, StringComparison.Ordinal))
                    {
                        return source;
                    }
                }
            }

            ancestor = GetLayoutParent(ancestor);
        }

        return current;
    }

    private static DependencyObject? GetLayoutParent(DependencyObject element)
    {
        if (element is FrameworkElement { TemplatedParent: { } templatedParent })
        {
            return templatedParent;
        }

        try
        {
            return VisualTreeHelper.GetParent(element);
        }
        catch (InvalidOperationException)
        {
            return LogicalTreeHelper.GetParent(element);
        }
    }

    private void OnTargetPropertyChanged(PropertyRegistration registration)
    {
        if (disposed || applying)
        {
            return;
        }

        if (registration.Owner.GetValue(registration.Property) is not string source)
        {
            registration.SourceText = null;
            return;
        }

        registration.SourceText = source;
        Apply(registration);
    }

    private void ApplyAll()
    {
        foreach (var registration in registrations.Values)
        {
            foreach (var property in registration.Properties)
            {
                Apply(property);
            }
        }

        foreach (var comboBox in registrations.Keys.OfType<ComboBox>())
        {
            ApplyComboBoxSelection(comboBox);
        }
    }

    private void ApplyComboBoxSelection(ComboBox comboBox)
    {
        if (disposed || comboBox.SelectedItem is null)
        {
            return;
        }

        comboBox.ApplyTemplate();
        if (comboBox.Template?.FindName("ContentSite", comboBox) is not ContentPresenter presenter
            || GetComboBoxSelectionSource(comboBox) is not { Length: > 0 } source)
        {
            return;
        }

        var target = CurrentLanguage == UiLanguage.English && ShouldTranslate(source)
            ? EnglishUiText.Translate(source)
            : source;
        applying = true;
        try
        {
            presenter.SetValue(ContentPresenter.ContentTemplateProperty, null);
            presenter.SetValue(ContentPresenter.ContentTemplateSelectorProperty, null);
            presenter.SetValue(ContentPresenter.ContentStringFormatProperty, null);
            presenter.SetValue(ContentPresenter.ContentProperty, target);
        }
        finally
        {
            applying = false;
        }
    }

    private static string? GetComboBoxSelectionSource(ComboBox comboBox)
    {
        var selectedItem = comboBox.SelectedItem;
        if (!string.IsNullOrWhiteSpace(comboBox.DisplayMemberPath)
            && TypeDescriptor.GetProperties(selectedItem)[comboBox.DisplayMemberPath]?.GetValue(selectedItem)
                is { } displayValue)
        {
            return Convert.ToString(displayValue, CultureInfo.CurrentCulture);
        }

        foreach (var propertyName in new[] { "Label", "Title", "Preview", "DisplayLabel" })
        {
            if (TypeDescriptor.GetProperties(selectedItem)[propertyName]?.GetValue(selectedItem) is { } value)
            {
                return Convert.ToString(value, CultureInfo.CurrentCulture);
            }
        }

        return comboBox.SelectionBoxItem as string
            ?? Convert.ToString(comboBox.SelectionBoxItem, CultureInfo.CurrentCulture);
    }

    private void Apply(PropertyRegistration registration)
    {
        if (registration.SourceText is not { } source)
        {
            return;
        }

        var target = CurrentLanguage == UiLanguage.English && ShouldTranslate(source)
            ? EnglishUiText.Translate(source)
            : source;

        if (string.Equals(registration.Owner.GetValue(registration.Property) as string, target, StringComparison.Ordinal))
        {
            return;
        }

        applying = true;
        try
        {
            registration.Owner.SetCurrentValue(registration.Property, target);
        }
        finally
        {
            applying = false;
        }
    }

    private static bool ShouldTranslate(string source)
    {
        return EnglishUiText.ContainsChinese(source) && !LooksLikePathOrUri(source);
    }

    private static bool LooksLikePathOrUri(string source)
    {
        var trimmed = source.Trim();
        return trimmed.StartsWith("\\\\", StringComparison.Ordinal)
            || trimmed.StartsWith("/", StringComparison.Ordinal)
            || (trimmed.Length >= 3
                && char.IsAsciiLetter(trimmed[0])
                && trimmed[1] == ':'
                && (trimmed[2] == '\\' || trimmed[2] == '/'))
            || Uri.TryCreate(trimmed, UriKind.Absolute, out _);
    }

    private sealed class ElementRegistration(DependencyObject owner) : IDisposable
    {
        internal DependencyObject Owner { get; } = owner;

        internal List<PropertyRegistration> Properties { get; } = [];

        internal List<Action> DetachActions { get; } = [];

        public void Dispose()
        {
            foreach (var property in Properties)
            {
                property.Dispose();
            }

            Properties.Clear();
            foreach (var detach in DetachActions)
            {
                detach();
            }

            DetachActions.Clear();
        }
    }

    private sealed class PropertyRegistration : IDisposable
    {
        private readonly DependencyPropertyDescriptor descriptor;
        private readonly EventHandler handler;
        private readonly Action<PropertyRegistration> onChanged;
        private bool attached;

        internal PropertyRegistration(
            DependencyObject owner,
            DependencyProperty property,
            DependencyPropertyDescriptor descriptor,
            string? sourceText,
            Action<PropertyRegistration> onChanged)
        {
            Owner = owner;
            Property = property;
            this.descriptor = descriptor;
            SourceText = sourceText;
            this.onChanged = onChanged;
            handler = (_, _) => this.onChanged(this);
        }

        internal DependencyObject Owner { get; }

        internal DependencyProperty Property { get; }

        internal string? SourceText { get; set; }

        internal void Attach()
        {
            descriptor.AddValueChanged(Owner, handler);
            attached = true;
        }

        public void Dispose()
        {
            if (!attached)
            {
                return;
            }

            descriptor.RemoveValueChanged(Owner, handler);
            attached = false;
        }
    }
}
