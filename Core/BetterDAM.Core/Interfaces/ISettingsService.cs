using BetterDAM.Core.Models;

namespace BetterDAM.Core.Interfaces;

public interface ISettingsService
{
    AppSettings Current { get; }

    /// <summary>Raised after <see cref="SaveAsync"/> so dependants can react to a changed setting.</summary>
    event EventHandler<AppSettings>? Changed;

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
