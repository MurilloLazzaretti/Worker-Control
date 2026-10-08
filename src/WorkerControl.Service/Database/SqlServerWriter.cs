using Microsoft.Data.SqlClient;

namespace WorkerControl.Service.Database;

/// <summary>
/// Applies the script of an item of a package to a SQL Server database. Each item is one
/// transaction: all of it is done, or none.
/// </summary>
internal sealed class SqlServerWriter : IDatabaseWriter
{
    private const int CommandSeconds = 300;

    public async Task<ApplyResult> ApplyAsync(DatabaseConnection connection, string database, ApplyRequest request, CancellationToken stopping)
    {
        var messages = new List<string>();
        await using var sql = new SqlConnection(SqlServerSource.ConnectionString(connection, database));
        sql.InfoMessage += (_, said) => messages.Add(said.Message);
        try
        {
            await sql.OpenAsync(stopping);
        }
        catch (SqlException error)
        {
            return new ApplyResult(false, null, error.Message, null, null, messages);
        }

        string did;
        List<string> batches;
        try
        {
            (did, batches) = await Plan(sql, request, stopping);
        }
        catch (Refusal refusal)
        {
            return new ApplyResult(false, null, refusal.Message, null, null, messages);
        }
        if (batches.Count == 0)
            return new ApplyResult(true, did, null, null, null, messages);

        await using var transaction = (SqlTransaction)await sql.BeginTransactionAsync(stopping);
        var batch = 0;
        try
        {
            await Run(sql, transaction, "SET XACT_ABORT ON;", stopping);
            foreach (var text in batches)
            {
                batch++;
                await Run(sql, transaction, text, stopping);
            }
            await transaction.CommitAsync(stopping);
            return new ApplyResult(true, did, null, null, null, messages);
        }
        catch (SqlException error)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception undo) when (undo is SqlException or InvalidOperationException)
            {
                // The instance already undid it, or the connection is gone and it will.
            }
            return new ApplyResult(false, null, error.Message, batch, error.LineNumber, messages);
        }
    }

    private sealed class Refusal(string message) : Exception(message);

    /// <summary>
    /// What is to be run, decided by looking at what is there: an object that exists is altered.
    /// </summary>
    private static async Task<(string Did, List<string> Batches)> Plan(SqlConnection sql, ApplyRequest request, CancellationToken stopping)
    {
        if (request.Action == "Script")
            return ("ran", SqlApply.Batches(request.Script ?? ""));

        if (request.Kind is not { } kind || request.Schema is not { } schema || request.Name is not { } name)
            throw new Refusal("The item does not say which object it is about");
        var exists = await Exists(sql, kind, schema, name, stopping);

        if (request.Action == "Drop")
            return exists ? ("dropped", [SqlApply.Drop(kind, request.Variety, schema, name)]) : ("absent", []);

        if (request.Action != "Define")
            throw new Refusal($"The action {request.Action} is not known");
        if (string.IsNullOrWhiteSpace(request.Script))
            throw new Refusal("The item has no script");

        if (kind is "Table" or "Type")
        {
            // Neither can be altered from the script that creates it without risking what is in it or uses it.
            if (exists)
                throw new Refusal($"The {kind.ToLowerInvariant()} {schema}.{name} already exists here; changing it takes a script written for that");
            return ("created", SqlApply.Batches(request.Script));
        }

        var batches = SqlApply.Batches(request.Script);
        if (batches.Count == 0 || SqlApply.AsCreate(batches[0], exists) is not { } first)
            throw new Refusal($"The script of {schema}.{name} does not begin by creating or altering it");
        batches[0] = first;
        return (exists ? "altered" : "created", batches);
    }

    private static async Task<bool> Exists(SqlConnection sql, string kind, string schema, string name, CancellationToken stopping)
    {
        await using var command = sql.CreateCommand();
        command.CommandText = kind == "Type"
            ? "SELECT COUNT(*) FROM sys.types t WHERE t.is_user_defined = 1 AND t.name = @name AND SCHEMA_NAME(t.schema_id) = @schema"
            : "SELECT COUNT(*) FROM sys.objects o WHERE o.name = @name AND SCHEMA_NAME(o.schema_id) = @schema AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF')";
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@schema", schema);
        return Convert.ToInt32(await command.ExecuteScalarAsync(stopping)) > 0;
    }

    private static async Task Run(SqlConnection sql, SqlTransaction transaction, string text, CancellationToken stopping)
    {
        await using var command = sql.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        command.CommandTimeout = CommandSeconds;
        await command.ExecuteNonQueryAsync(stopping);
    }
}
