using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HelmetDesk.Core.Finance
{
    /// <summary>Base of the Finanças view models: raises <see cref="PropertyChanged"/> when a value really changes.</summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
