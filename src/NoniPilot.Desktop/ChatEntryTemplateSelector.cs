using System.Windows;
using System.Windows.Controls;

namespace NoniPilot.Desktop;

public sealed class ChatEntryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate { get; set; }
    public DataTemplate? AssistantTemplate { get; set; }
    public DataTemplate? SystemStepTemplate { get; set; }
    public DataTemplate? ErrorTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        ChatEntry { Kind: ChatEntryKind.User } => UserTemplate,
        ChatEntry { Kind: ChatEntryKind.Assistant } => AssistantTemplate,
        ChatEntry { Kind: ChatEntryKind.SystemStep } => SystemStepTemplate,
        ChatEntry { Kind: ChatEntryKind.Error } => ErrorTemplate,
        _ => AssistantTemplate,
    };
}
