﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Threading;
using System.IO;
using System.Diagnostics;

namespace Cassandra.Native
{
    public class CassandraSession : IDisposable
    {
        CredentialsDelegate credentialsDelegate;

        CassandraCompressionType compression;
        int abortTimeout;

        List<string> loadedClusterEndpoints;
        List<string> upClusterEndpoints;
        List<WeakReference<CassandraConnection>> connectionPool = new List<WeakReference<CassandraConnection>>();
        int maxConnectionsInPool = int.MaxValue;
        string keyspace = string.Empty;



        CassandraConnection eventRaisingConnection = null;

        public CassandraSession(IEnumerable<IPEndPoint> clusterEndpoints, string keyspace, CassandraCompressionType compression = CassandraCompressionType.NoCompression,
            int abortTimeout = Timeout.Infinite, CredentialsDelegate credentialsDelegate = null, int maxConnectionsInPool = int.MaxValue)
        {
            this.maxConnectionsInPool = maxConnectionsInPool;
            
            this.loadedClusterEndpoints = new List<string>();
            foreach (var ep in clusterEndpoints)
                loadedClusterEndpoints.Add(ep.ToString());

            this.upClusterEndpoints = new List<string>(loadedClusterEndpoints);

            this.compression = compression;
            this.abortTimeout = abortTimeout;

            this.credentialsDelegate = credentialsDelegate;
            this.keyspace = keyspace;
            setupEventListeners(connect());
        }

        private void setupEventListeners(CassandraConnection nconn)
        {
            Exception theExc = null;

            nconn.CassandraEvent += new CassandraEventHandler(conn_CassandraEvent);
            using (var ret = nconn.RegisterForCassandraEvent(
                CassandraEventType.TopologyChange | CassandraEventType.StatusChange))
            {
                if (!(ret is OutputVoid))
                {
                    if (ret is OutputError)
                        theExc = new Exception("CQL Error [" + (ret as OutputError).CassandraErrorType.ToString() + "] " + (ret as OutputError).Message);
                    else
                        theExc = new InvalidOperationException();
                }
            }

            if (theExc != null)
                throw new CassandraConnectionException("Register event", theExc);

            eventRaisingConnection = nconn;
        }

        private CassandraConnection connect()
        {
            checkDisposed();

            lock (connectionPool)
            {
            retry:
                if (connectionPool.Count > 0)
                {
                    var conn = connectionPool[StaticRandom.Instance.Next(connectionPool.Count)];
                    if (!conn.IsAlive)
                    {
                        connectionPool.Remove(conn);
                        goto retry;
                    }
                    else
                    {
                        if (!conn.Value.IsHealthy)
                        {
                            var recoveryEvents = (eventRaisingConnection == conn.Value);
                            conn.Value.Dispose();
                            connectionPool.Remove(conn);
                            if (recoveryEvents)
                                setupEventListeners(connect());
                            goto retry;
                        }
                        else
                            return conn.Value;
                    }
                }
            }

            IPEndPoint endPoint = null;
            lock (upClusterEndpoints)
                endPoint = IPEndPointParser.ParseEndpoint(upClusterEndpoints[StaticRandom.Instance.Next(upClusterEndpoints.Count)]);

            CassandraConnection nconn = null;

            try
            {
                nconn = new CassandraConnection(endPoint, credentialsDelegate, this.compression, this.abortTimeout);

                var options = nconn.ExecuteOptions();

                if (!string.IsNullOrEmpty(keyspace))
                {
                    if (processScallar(nconn.Query("USE " + CqlQueryTools.CqlIdentifier(keyspace),CqlConsistencyLevel.DEFAULT)).ToString() != keyspace)
                        throw new InvalidOperationException();
                }

            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message, "CassandraManager.Connect");
                if (nconn != null)
                    nconn.Dispose();
                throw new CassandraConnectionException("Cannot connect", ex);
            }

            lock (connectionPool)
            {
                connectionPool.Add(new WeakReference<CassandraConnection>(nconn));
                return nconn;
            }
        }

        public void ChangeKeyspace(string keyspace)
        {
            lock (connectionPool)
            {
                foreach (var conn in connectionPool)
                {
                    if (conn.IsAlive && conn.Value.IsHealthy)
                    {
                    retry:
                        try
                        {
                            if (processScallar(conn.Value.Query("USE \"" + keyspace.Replace("\"", "\"\"") + "\"", CqlConsistencyLevel.DEFAULT)).ToString() != keyspace)
                                throw new InvalidOperationException();
                        }
                        catch (Cassandra.Native.CassandraConnection.StreamAllocationException)
                        {
                            goto retry;
                        }
                    }
                }
                this.keyspace = keyspace;
            }
        }

        void conn_CassandraEvent(object sender, CassandraEventArgs e)
        {
            if (e.CassandraEventType == CassandraEventType.StatusChange || e.CassandraEventType == CassandraEventType.TopologyChange)
            {
                if (e.Message == "UP" || e.Message == "NEW_NODE")
                {
                    lock (upClusterEndpoints)
                        if (!loadedClusterEndpoints.Contains(e.IPEndPoint.ToString()))
                            upClusterEndpoints.Add(e.IPEndPoint.ToString());
                    return;
                }
                else if (e.Message == "DOWN" || e.Message == "REMOVED_NODE")
                {
                    lock (upClusterEndpoints)
                        if (!upClusterEndpoints.Contains(e.IPEndPoint.ToString()))
                            upClusterEndpoints.Remove(e.IPEndPoint.ToString());
                    return;
                }
            }
            throw new InvalidOperationException();
        }

        Guarded<bool> alreadyDisposed = new Guarded<bool>(false);

        void checkDisposed()
        {
            lock (alreadyDisposed)
                if (alreadyDisposed.Value)
                    throw new ObjectDisposedException("CassandraSession");
        }

        public void Dispose()
        {
            lock (alreadyDisposed)
            {
                if (alreadyDisposed.Value)
                    return;
                alreadyDisposed.Value = true;
                lock (connectionPool)
                {
                    foreach (var conn in connectionPool)
                    {
                        if (conn.IsAlive)
                            conn.Value.Dispose();
                    }
                }
            }
        }

        ~CassandraSession()
        {
            Dispose();
        }

        class ConnectionWrapper
        {
            public CassandraConnection connection;
        }

        private void processNonQuery(IOutput outp)
        {
            using (outp)
            {
                if (outp is OutputError)
                    throw (outp as OutputError).CreateException();
                else if (outp is OutputVoid)
                    return;
                else if (outp is OutputSchemaChange)
                    return;
                else
                    throw new InvalidOperationException();
            }
        }

        private object processScallar(IOutput outp)
        {
            using (outp)
            {
                if (outp is OutputError)
                    throw (outp as OutputError).CreateException();
                else if (outp is OutputSetKeyspace)
                    return (outp as OutputSetKeyspace).Value;
                else
                    throw new InvalidOperationException();
            }
        }

        private byte[] processEndPrepare(IOutput outp, out Metadata metadata)
        {
            using (outp)
            {
                if (outp is OutputError)
                    throw (outp as OutputError).CreateException();
                else if (outp is OutputPrepared)
                {
                    metadata = (outp as OutputPrepared).Metadata;
                    return (outp as OutputPrepared).QueryID;
                }
                else
                    throw new InvalidOperationException();
            }
        }
        

        private CqlRowSet processRowset(IOutput outp)
        {
            if (outp is OutputError)
            {
                try
                {
                    throw (outp as OutputError).CreateException();
                }
                finally
                {
                    outp.Dispose();
                }
            }
            else if (outp is OutputRows)
            {
                return new CqlRowSet(outp as OutputRows, true);
            }
            else
                throw new InvalidOperationException();
        }

        public IAsyncResult BeginNonQuery(string cqlQuery, AsyncCallback callback, object state, CqlConsistencyLevel consistency)
        {
        retry:
            try
            {
                var c = new ConnectionWrapper() { connection = connect() };
                return c.connection.BeginQuery(cqlQuery, callback, state, c, consistency);
            }
            catch (Cassandra.Native.CassandraConnection.StreamAllocationException)
            {
                goto retry;
            }
        }

        public void EndNonQuery(IAsyncResult result)
        {
            var c = (ConnectionWrapper)((Internal.AsyncResult<IOutput>)result).AsyncOwner;
            processNonQuery(c.connection.EndQuery(result, c));
        }

        public void NonQuery(string cqlQuery, CqlConsistencyLevel consistency)
        {
        retry:
            try
             {
                var connection = connect();
                processNonQuery(connection.Query(cqlQuery, consistency));
            }
            catch (Cassandra.Native.CassandraConnection.StreamAllocationException)
            {
                goto retry;
            }
        }

        public IAsyncResult BeginScalar(string cqlQuery, CqlConsistencyLevel consistency, AsyncCallback callback, object state)
        {
        retry:
            try
            {
                var c = new ConnectionWrapper() { connection = connect() };
                return c.connection.BeginQuery(cqlQuery, callback, state, c, consistency);
            }
            catch (Cassandra.Native.CassandraConnection.StreamAllocationException)
            {
                goto retry;
            }
        }

        public object EndScalar(IAsyncResult result)
        {
            var c = (ConnectionWrapper)((Internal.AsyncResult<IOutput>)result).AsyncOwner;
            return processScallar(c.connection.EndQuery(result, c));
        }

        public object Scalar(string cqlQuery)
        {
        retry:
            try
            {
                var connection = connect();
                return processScallar(connection.Query(cqlQuery,CqlConsistencyLevel.DEFAULT));
            }
            catch (Cassandra.Native.CassandraConnection.StreamAllocationException)
            {
                goto retry;
            }
        }

        public IAsyncResult BeginQuery(string cqlQuery, AsyncCallback callback, object state, CqlConsistencyLevel consistency, bool delayedRelease = false)
        {
        retry:
            try
            {
                var c = new ConnectionWrapper() { connection = connect() };
                return c.connection.BeginQuery(cqlQuery, callback, state, c, consistency);
            }
            catch (Cassandra.Native.CassandraConnection.StreamAllocationException)
            {
                goto retry;
            }
        }

        public CqlRowSet EndQuery(IAsyncResult result)
        {
            var c = (ConnectionWrapper)((Internal.AsyncResult<IOutput>)result).AsyncOwner;
            return processRowset(c.connection.EndQuery(result, c));
        }

        public CqlRowSet Query(string cqlQuery, CqlConsistencyLevel consistency)
        {
        retry:
            try
            {
                var connection = connect();
                return processRowset(connection.Query(cqlQuery, consistency));
            }
            catch (Cassandra.Native.CassandraConnection.StreamAllocationException)
            {
                goto retry;
            }
        }

        public IAsyncResult BeginPrepareQuery(string cqlQuery, AsyncCallback callback, object state, bool delayedRelease = false)
        {
        retry:
            try
            {
                var c = new ConnectionWrapper() { connection = connect() };
                return c.connection.BeginPrepareQuery(cqlQuery, callback, state, c);
            }
            catch (Cassandra.Native.CassandraConnection.StreamAllocationException)
            {
                goto retry;
            }
        }

        public byte[] EndPrepareQuery(IAsyncResult result, out Metadata metadata)
        {
            var c = (ConnectionWrapper)((Internal.AsyncResult<IOutput>)result).AsyncOwner;
            return processEndPrepare(c.connection.EndPrepareQuery(result, c), out metadata);
        }

        public byte[] PrepareQuery(string cqlQuery, out Metadata metadata)
        {
        retry:
            try
            {
                var connection = connect();
                return processEndPrepare(connection.PrepareQuery(cqlQuery), out metadata);
            }
            catch (Cassandra.Native.CassandraConnection.StreamAllocationException)
            {
                goto retry;
            }
        }

        public IAsyncResult BeginExecuteQuery(byte[] Id, Metadata Metadata, object[] values, AsyncCallback callback, object state, bool delayedRelease, CqlConsistencyLevel consistency)
        {
        retry:
            try
            {
                var c = new ConnectionWrapper() { connection = connect() };
                return c.connection.BeginExecuteQuery(Id, Metadata, values, callback, state, c, consistency);
            }
            catch (Cassandra.Native.CassandraConnection.StreamAllocationException)
            {
                goto retry;
            }
        }

        public CqlRowSet EndExecuteQuery(IAsyncResult result)
        {
            var c = (ConnectionWrapper)((Internal.AsyncResult<IOutput>)result).AsyncOwner;
            return processRowset(c.connection.EndExecuteQuery(result, c));
        }

        public void ExecuteQuery(byte[] Id, Metadata Metadata, object[] values, CqlConsistencyLevel consistency)
        {
        retry:
            try
            {
                var connection = connect();
                //return processRowset(connection.ExecuteQuery(Id, Metadata, values));
                processNonQuery(connection.ExecuteQuery(Id, Metadata, values, consistency));
            }
            catch (Cassandra.Native.CassandraConnection.StreamAllocationException)
            {
                goto retry;
            }
        }


    }
}