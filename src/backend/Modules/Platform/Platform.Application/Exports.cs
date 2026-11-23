namespace RetailCloud.Platform.Application;

/// <summary>TEN-005 / US-016: the data categories of a full export (D07 ExportCreate.categories).</summary>
public static class ExportCategories
{
    public static readonly IReadOnlyList<string> All = ["Products", "Customers", "Sales", "Inventory", "Journals", "Receivables", "TaxInvoices"];
}

public sealed record ExportRequest(IReadOnlyList<string> Categories, DateTimeOffset? From, DateTimeOffset? To);

public sealed record ExportJobView(Guid Id, string Status, IReadOnlyList<string> Categories, DateTimeOffset? From, DateTimeOffset? To,
    long? RowCount, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, DateTimeOffset? ExpiresAt, string? ObjectKey, string? Error);

public interface IExportJobs
{
    /// <summary>Records the request and its platform.export-requested event in the caller's unit of work.</summary>
    Task<ExportJobView> CreateAsync(BuildingBlocks.Application.IUnitOfWorkScope scope, Guid requestedBy, ExportRequest request, CancellationToken cancellationToken);

    Task<ExportJobView?> GetAsync(BuildingBlocks.Application.IUnitOfWorkScope scope, Guid id, CancellationToken cancellationToken);

    /// <summary>TM-015: one export at a time per tenant.</summary>
    Task<bool> HasActiveAsync(BuildingBlocks.Application.IUnitOfWorkScope scope, CancellationToken cancellationToken);
}
