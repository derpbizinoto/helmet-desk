using System;
using System.Windows.Markup;

namespace HelmetDesk
{
    /// <summary>
    /// XAML text in the current language: <c>Text="{local:Tr Configurações}"</c>, or quoted when the text has a comma:
    /// <c>Text="{local:Tr 'Notas, tarefas'}"</c>. The language is fixed for the whole run (changing it restarts the app).
    /// </summary>
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class TrExtension : MarkupExtension
    {
        public TrExtension() { }
        public TrExtension(string text) { Text = text; }

        [ConstructorArgument("text")]
        public string Text { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider) => T(Text);
    }
}
