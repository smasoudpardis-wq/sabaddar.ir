using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.BuildingBlocks.Infrastructure;

/// <summary>
/// PostgreSQL unit of work (D04 §5.3):
/// <list type="bullet">
/// <item>one connection and one READ COMMITTED transaction shared by all module DbContexts and raw SQL;</item>
/// <item><c>set_config('app.tenant_id', …, true)</c> right after BEGIN — transaction-local (= SET LOCAL), so it is
/// cleared on COMMIT/ROLLBACK and is safe with PgBouncer transaction pooling;</item>
/// <item>commit only when the work completes; any exception rolls everything back.</item>
/// </list>
/// This is the only place allowed to set <c>app.tenant_id</c> (architecture rule R9).
/// </summary>
public sealed class PostgresUnitOfWork(NpgsqlDataSource dataSource) : IUnitOfWork
{
    public Task<T> ExecuteAsync<T>(ITenantContext tenant, Func<IUnitOfWorkScope, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return RunAsync(tenant.TenantId, tenant.UserId, work, cancellationToken);
    }

    public Task<T> ExecuteWithoutTenantAsync<T>(UserId? user, Func<IUnitOfWorkScope, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default) =>
        RunAsync(null, user, work, cancellationToken);

    private async Task<T> RunAsync<T>(TenantId? tenantId, UserId? userId, Func<IUnitOfWorkScope, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await using (var set = new NpgsqlCommand(
                         "SELECT set_config('app.tenant_id', $1, true), set_config('app.user_id', $2, true)",
                         connection, transaction))
        {
            set.Parameters.Add(new NpgsqlParameter { Value = tenantId?.Value.ToString() ?? string.Empty });
            set.Parameters.Add(new NpgsqlParameter { Value = userId?.Value.ToString() ?? string.Empty });
            await set.ExecuteNonQueryAsync(cancellationToken);
        }

        var scope = new PostgresUnitOfWorkScope(tenantId, connection, transaction);
        try
        {
            T result = await work(scope, cancellationToken);
            await scope.SaveAllAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }
}

/// <summary>Scope exposing the shared connection/transaction to module infrastructure (never to Domain code).</summary>
public sealed class PostgresUnitOfWorkScope : IUnitOfWorkScope, IAsyncDisposable
{
    private readonly List<DbContext> _contexts = [];

    internal PostgresUnitOfWorkScope(TenantId? tenantId, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        TenantId = tenantId;
        Connection = connection;
        Transaction = transaction;
    }

    public TenantId? TenantId { get; }

    public NpgsqlConnection Connection { get; }

    public NpgsqlTransaction Transaction { get; }

    /// <summary>
    /// Creates a module DbContext bound to the shared connection and transaction. Changes are saved by the unit of work
    /// before commit, in creation order; handlers may also call SaveChangesAsync earlier to surface constraint errors.
    /// </summary>
    public TContext Context<TContext>(Func<DbContextOptions<TContext>, TContext> factory)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(factory);
        TContext? existing = _contexts.OfType<TContext>().FirstOrDefault();
        if (existing is not null)
        {
            return existing;
        }

        DbContextOptions<TContext> options = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(Connection)
            .AddInterceptors(RequireTransactionInterceptor.Instance)
            .Options;
        TContext context = factory(options);
        context.Database.UseTransaction(Transaction);
        _contexts.Add(context);
        return context;
    }

    /// <summary>Creates (or returns) the module DbContext via its <c>(DbContextOptions&lt;TContext&gt;)</c> constructor.</summary>
    public TContext Context<TContext>()
        where TContext : DbContext =>
        Context<TContext>(static options =>
            (TContext)(Activator.CreateInstance(typeof(TContext), options)
                       ?? throw new InvalidOperationException($"{typeof(TContext).Name} needs a (DbContextOptions) constructor.")));

    public NpgsqlCommand Command(string sql) => new(sql, Connection, Transaction);

    internal async Task SaveAllAsync(CancellationToken cancellationToken)
    {
        foreach (DbContext context in _contexts)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (DbContext context in _contexts)
        {
            await context.DisposeAsync();
        }

        _contexts.Clear();
    }
}

/// <summary>Fail fast if any EF command runs outside the unit-of-work transaction (D04 §5.3 guard).</summary>
public sealed class RequireTransactionInterceptor : DbCommandInterceptor
{
    public static readonly RequireTransactionInterceptor Instance = new();

    private static void Check(DbCommand command)
    {
        if (command.Transaction is null)
        {
            throw new InvalidOperationException(
                "Database command executed outside the unit-of-work transaction; tenant context would be missing.");
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }
}
