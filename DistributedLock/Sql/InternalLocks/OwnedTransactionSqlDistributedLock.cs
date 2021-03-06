﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Medallion.Threading.Sql
{
    internal sealed class OwnedTransactionSqlDistributedLock : IInternalSqlDistributedLock
    {
        private readonly string lockName, connectionString;

        public OwnedTransactionSqlDistributedLock(string lockName, string connectionString)
        {
            this.lockName = lockName;
            this.connectionString = connectionString;
        }

        public IDisposable TryAcquire(int timeoutMillis, SqlApplicationLock.Mode mode, IDisposable contextHandle)
        {
            if (contextHandle != null)
            {
                return this.CreateContextLock(contextHandle).TryAcquire(timeoutMillis, mode, contextHandle: null);
            }

            IDisposable result = null;
            var connection = new SqlConnection(this.connectionString);
            SqlTransaction transaction = null;
            try
            {
                connection.Open();
                // when creating a transaction, the isolation level doesn't matter, since we're using sp_getapplock
                transaction = connection.BeginTransaction();
                if (SqlApplicationLock.ExecuteAcquireCommand(transaction, this.lockName, timeoutMillis, mode))
                {
                    result = new LockScope(transaction);
                }
            }
            finally
            {
                // if we fail to acquire or throw, make sure to clean up
                if (result == null)
                {
                    transaction?.Dispose();
                    connection.Dispose();
                }
            }

            return result;
        }

        public async Task<IDisposable> TryAcquireAsync(int timeoutMillis, SqlApplicationLock.Mode mode, CancellationToken cancellationToken, IDisposable contextHandle = null)
        {
            if (contextHandle != null)
            {
                return await this.CreateContextLock(contextHandle).TryAcquireAsync(timeoutMillis, mode, cancellationToken, contextHandle: null).ConfigureAwait(false);
            }

            IDisposable result = null;
            var connection = new SqlConnection(this.connectionString);
            SqlTransaction transaction = null;
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                // when creating a transaction, the isolation level doesn't matter, since we're using sp_getapplock
                transaction = connection.BeginTransaction();
                if (await SqlApplicationLock.ExecuteAcquireCommandAsync(transaction, this.lockName, timeoutMillis, mode, cancellationToken).ConfigureAwait(false))
                {
                    result = new LockScope(transaction);
                }
            }
            finally
            {
                // if we fail to acquire or throw, make sure to clean up
                if (result == null)
                {
                    transaction?.Dispose();
                    connection.Dispose();
                }
            }

            return result;
        }

        private IInternalSqlDistributedLock CreateContextLock(IDisposable contextHandle)
        {
            var transaction = ((LockScope)contextHandle).Transaction;
            if (transaction == null) { throw new ObjectDisposedException(nameof(contextHandle), "the provided handle is already disposed"); }

            return new TransactionScopedSqlDistributedLock(this.lockName, transaction);
        } 

        private sealed class LockScope : IDisposable
        {
            private SqlTransaction transaction;

            public LockScope(SqlTransaction transaction)
            {
                this.transaction = transaction;
            }

            public SqlTransaction Transaction => Volatile.Read(ref this.transaction);

            public void Dispose()
            {
                var transaction = Interlocked.Exchange(ref this.transaction, null);
                if (transaction != null)
                {
                    var connection = transaction.Connection;
                    transaction.Dispose(); // first end the transaction to release the lock
                    connection.Dispose(); // then close the connection (returns it to the pool)
                }
            }
        }
    }
}
