using QuantDesk.Modules.Portfolio.Application.Contracts;

namespace QuantDesk.Modules.Portfolio.Application.Abstractions;

/// <summary>Caller must authenticate the owner. Account-wide read, not session authorization.</summary>
public interface IPortfolioSnapshotReader
{
    Task<PortfolioAccountSnapshot?> ReadOwnedAsync(Guid ownerUserId, Guid accountId,
        CancellationToken cancellationToken = default);
}
