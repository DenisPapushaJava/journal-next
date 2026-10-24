using System.Data;
using System.Data.OleDb;
using Jornal.Core.Orders;
using Jornal.Core.Pricing;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Infrastructure.Access;

public sealed partial class OleDbMachineCatalog
{
    public Task<CuttingMachine> UpdateMachineImportAsync(MachineImportChange change, CancellationToken cancellationToken = default)
    {
        var snapshot = change.Snapshot();
        return !snapshot.HasChanges ? Task.FromResult(snapshot.Values) : Task.Run(() => UpdateImport(snapshot, cancellationToken), cancellationToken);
    }

    private CuttingMachine UpdateImport(MachineImportChange change, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        OleDbConnection? connection = null;
        OleDbTransaction? transaction = null;
        var uncertain = false;
        try
        {
            token.ThrowIfCancellationRequested();
            connection = OleDbAccessConnection.Create(path, writable: true);
            DiagnosticStages.Run(DiagnosticStage.OpenDatabase, connection.Open);
            transaction = DiagnosticStages.Run(DiagnosticStage.BeginTransaction, () => connection.BeginTransaction(IsolationLevel.ReadCommitted));
            var current = Read(connection, transaction, token).SingleOrDefault(machine => machine.TableName == change.Expected.TableName);
            change.Verify(current);
            using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 10;
            command.CommandText = "UPDATE [JournalMachines] SET [SpecificationProfileId] = ? WHERE [PriceTableName] = ? AND [Name] = ? AND " +
                (change.Expected.SpecificationProfileId is null ? "[SpecificationProfileId] IS NULL" : "[SpecificationProfileId] = ?");
            command.Parameters.Add("", OleDbType.VarWChar, 64).Value = (object?)change.SpecificationProfileId ?? DBNull.Value;
            command.Parameters.Add("", OleDbType.VarWChar, 64).Value = change.Expected.TableName;
            command.Parameters.Add("", OleDbType.VarWChar, 255).Value = change.Expected.Name;
            if (change.Expected.SpecificationProfileId is { } profile) command.Parameters.Add("", OleDbType.VarWChar, 64).Value = profile;
            token.ThrowIfCancellationRequested();
            if (DiagnosticStages.Run(DiagnosticStage.WriteRecord, command.ExecuteNonQuery) != 1) throw MachineCreation.Conflict();
            var saved = DiagnosticStages.Run(DiagnosticStage.ReadBack, () => Read(connection, transaction, token))
                .SingleOrDefault(machine => machine.TableName == change.Expected.TableName);
            if (saved != change.Values) throw new InvalidOperationException("Привязка импорта не подтверждена.");
            token.ThrowIfCancellationRequested(); uncertain = true;
            DiagnosticStages.Run(DiagnosticStage.CommitTransaction, transaction.Commit);
            return saved;
        }
        catch (Exception exception)
        {
            if (transaction is not null)
                try { DiagnosticStages.Run(DiagnosticStage.RollbackTransaction, transaction.Rollback); }
                catch (Exception) { uncertain = true; }
            if (uncertain) throw new OrderWriteException(OrderWriteFailure.OutcomeUnknown,
                "Сохранение не подтверждено. Обновите станок перед повтором.", exception);
            if (exception is OrderWriteException) throw;
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            throw new OrderWriteException(OrderWriteFailure.NotSaved, DataSourceAccessErrors.Describe(path, exception,
                "Импорт спецификаций не изменён. Повторите сохранение."), exception);
        }
        finally
        {
            try { transaction?.Dispose(); } catch (Exception) { }
            try { connection?.Dispose(); } catch (Exception) { }
        }
    }
}
