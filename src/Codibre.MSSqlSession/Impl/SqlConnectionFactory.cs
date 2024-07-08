﻿using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MyDotey.ObjectPool;
using MyDotey.ObjectPool.Facade;

namespace Codibre.MSSqlSession.Impl;
internal class ConnToken
{
    internal IEntry<SqlConnection> Entry { get; set; }
    internal ParsedConnInfo Key { get; set; }
    public ConnToken(IEntry<SqlConnection> entry, ParsedConnInfo key)
    {
        Entry = entry;
        Key = key;
    }
}

internal class ParsedConnInfo
{
    internal bool Pooling { get; }
    internal int MinPoolSize { get; }
    internal int MaxPoolSize { get; }
    internal string ConnectionString { get; }
    internal Func<SqlConnection> Create { get; }

    internal ParsedConnInfo(
        bool pooling,
        int minPoolSize,
        int maxPoolSize,
        string connectionString
    )
    {
        Pooling = pooling;
        MinPoolSize = minPoolSize;
        MaxPoolSize = maxPoolSize;
        ConnectionString = connectionString;
        Create = () => new SqlConnection(connectionString);
    }

    public override bool Equals(object obj)
    {
        if (obj is not ParsedConnInfo p) return false;
        if (p.Pooling != Pooling) return false;
        if (p.MinPoolSize != MinPoolSize) return false;
        if (p.MaxPoolSize != MaxPoolSize) return false;
        if (p.ConnectionString != ConnectionString) return false;
        return true;
    }

    public override int GetHashCode()
        => (Pooling, MinPoolSize, MaxPoolSize, ConnectionString).GetHashCode();
}
internal static class SqlConnectionFactory
{
    private static readonly Dictionary<string, ParsedConnInfo> _parsedConnStringDict = new();
    private static readonly Dictionary<ParsedConnInfo, IObjectPool<SqlConnection>> _poolDict = new();
    private static ParsedConnInfo GetConnParsedInfo(string connectionString)
    {
        if (!_parsedConnStringDict.TryGetValue(connectionString, out var parsedConnectionString))
        {
            lock (_parsedConnStringDict)
            {
                if (!_parsedConnStringDict.TryGetValue(connectionString, out parsedConnectionString))
                {
                    var builder = new SqlConnectionStringBuilder(connectionString);
                    if (!builder.Pooling)
                    {
                        parsedConnectionString = new ParsedConnInfo(false, 1, 1, connectionString);
                    }
                    else
                    {
                        builder.Pooling = false;
                        var minPoolSize = builder.MinPoolSize;
                        builder.MinPoolSize = 1;
                        var maxPoolSize = builder.MaxPoolSize;
                        builder.MaxPoolSize = 1;
                        var newConnString = builder.ConnectionString;
                        parsedConnectionString = new ParsedConnInfo(
                            true,
                            minPoolSize,
                            maxPoolSize,
                            newConnString
                        );
                    }
                    _parsedConnStringDict[connectionString] = parsedConnectionString;
                }
            }
        }
        return parsedConnectionString;
    }

    public static (SqlConnection, object?) GetConnection(string connectionString, ILogger logger)
    {
        var connInfo = GetConnParsedInfo(connectionString);
        if (!connInfo.Pooling) return (connInfo.Create(), null);
        if (!_poolDict.TryGetValue(connInfo, out var pool))
        {
            lock (_poolDict)
            {
                if (!_poolDict.TryGetValue(connInfo, out pool))
                {
                    var builder = ObjectPools.NewAutoScaleObjectPoolConfigBuilder<SqlConnection>();
                    _ = builder
                        .SetMaxSize(connInfo.MaxPoolSize)
                        .SetMinSize(connInfo.MinPoolSize)
                        .SetObjectFactory(() =>
                        {
                            logger.LogDebug(
                                "New connection. Acquired: {AcquiredSize}, Available: {AvailableSize}",
                                _poolDict[connInfo].AcquiredSize,
                                _poolDict[connInfo].AvailableSize,
                                _poolDict
                            );
                            return new SqlConnection(connInfo.ConnectionString);
                        })
                        .SetOnClose((x) => AsyncDbSession.CloseConn(x.Object));
                    _poolDict[connInfo] = pool = ObjectPools.NewObjectPool(builder.Build());
                }
            }
        }
        var connectionPooledObject = pool.Acquire();
        var connection = connectionPooledObject.Object;
        return (connection, new ConnToken(
            connectionPooledObject,
            connInfo
        ));
    }

    public static void ReleaseConnection(object? token)
    {
        if (token is ConnToken info) _poolDict[info.Key].Release(info.Entry);
    }
}