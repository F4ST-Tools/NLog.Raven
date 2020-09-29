using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using NLog.Common;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;

namespace NLog.Raven
{
    /// <summary>
    /// NLog message target for RavenDB.
    /// </summary>
    [Target("Raven")]
    public class RavenTarget : Target
    {
        //private static readonly ConcurrentDictionary<MongoConnectionKey, IMongoCollection<BsonDocument>> _collectionCache = new ConcurrentDictionary<MongoConnectionKey, IMongoCollection<BsonDocument>>();
        private Func<AsyncLogEventInfo, dynamic> _createDocumentDelegate;
        private static readonly LogEventInfo _defaultLogEvent = NLog.LogEventInfo.CreateNullEvent();
        private IDocumentStore _connection;

        /// <summary>
        /// Initializes a new instance of the <see cref="RavenTarget"/> class.
        /// </summary>
        public RavenTarget()
        {
            Fields = new List<RavenField>();
            Properties = new List<RavenField>();
            IncludeDefaults = true;
            OptimizeBufferReuse = true;
            IncludeEventProperties = true;
        }

        /// <summary>
        /// Gets the fields collection.
        /// </summary>
        /// <value>
        /// The fields.
        /// </value>
        //[ArrayParameter(typeof(RavenField), "field")]
        public IList<RavenField> Fields { get; private set; }

        /// <summary>
        /// Gets the properties collection.
        /// </summary>
        /// <value>
        /// The properties.
        /// </value>
        //[ArrayParameter(typeof(RavenField), "property")]
        public IList<RavenField> Properties { get; private set; }

        /// <summary>
        /// Gets or sets the connection string name string.
        /// </summary>
        /// <value>
        /// The connection name string.
        /// </value>
        public string ServerAddress
        {
            get => (_serverAddress as SimpleLayout)?.Text;
            set => _serverAddress = value ?? string.Empty;
        }
        private Layout _serverAddress;

        /// <summary>
        /// Gets or sets a value indicating whether to use the default document format.
        /// </summary>
        /// <value>
        ///   <c>true</c> to use the default document format; otherwise, <c>false</c>.
        /// </value>
        public bool IncludeDefaults { get; set; }

        /// <summary>
        /// Gets or sets the name of the database.
        /// </summary>
        /// <value>
        /// The name of the database.
        /// </value>
        public string DatabaseName
        {
            get => (_databaseName as SimpleLayout)?.Text;
            set => _databaseName = value ?? string.Empty;
        }
        private Layout _databaseName;

        /// <summary>
        /// Gets or sets the size in bytes of the capped collection.
        /// </summary>
        /// <value>
        /// The size of the capped collection.
        /// </value>
        public long? CappedCollectionSize { get; set; }

        /// <summary>
        /// Gets or sets the capped collection max items.
        /// </summary>
        /// <value>
        /// The capped collection max items.
        /// </value>
        public long? CappedCollectionMaxItems { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to include per-event properties in the payload sent to RavenDB
        /// </summary>
        public bool IncludeEventProperties { get; set; }

        /// <summary>
        /// Initializes the target. Can be used by inheriting classes
        /// to initialize logging.
        /// </summary>
        /// <exception cref="NLog.NLogConfigurationException">Can not resolve RavenDB ConnectionString. Please make sure the ConnectionString property is set.</exception>
        protected override void InitializeTarget()
        {
            base.InitializeTarget();

            _connection = new DocumentStore()
            {
                Urls = new[] { ServerAddress },
                Database = DatabaseName,

                Conventions =
                {
                    FindCollectionName = type => type.Name==nameof(RavenDataBag)
                        ? "Log"
                        :DocumentConventions.DefaultGetCollectionName(type)
                }
            };
            _connection.Initialize();
        }

        /// <summary>
        /// Writes an array of logging events to the log target. By default it iterates on all
        /// events and passes them to "Write" method. Inheriting classes can use this method to
        /// optimize batch writes.
        /// </summary>
        /// <param name="logEvents">Logging events to be written out.</param>
        protected override void Write(IList<AsyncLogEventInfo> logEvents)
        {
            if (logEvents.Count == 0)
                return;

            try
            {
                if (_createDocumentDelegate == null)
                    _createDocumentDelegate = e => CreateDocument(e.LogEvent);

                var documents = logEvents.Select(_createDocumentDelegate);

                using (var bulk = _connection.BulkInsert())
                {
                    foreach (var entity in documents)
                    {
                        bulk.Store(entity);
                    }
                }

                for (int i = 0; i < logEvents.Count; ++i)
                    logEvents[i].Continuation(null);
            }
            catch (Exception ex)
            {
                InternalLogger.Error("Error when writing to RavenDB {0}", ex);

                if (ex.MustBeRethrownImmediately())
                    throw;

                for (int i = 0; i < logEvents.Count; ++i)
                    logEvents[i].Continuation(ex);

                if (ex.MustBeRethrown())
                    throw;
            }
        }

        /// <summary>
        /// Writes logging event to the log target.
        /// classes.
        /// </summary>
        /// <param name="logEvent">Logging event to be written out.</param>
        protected override void Write(LogEventInfo logEvent)
        {
            try
            {
                var document = CreateDocument(logEvent);
                using (var session = _connection.OpenSession())
                {
                    session.Store(document);
                    session.SaveChanges();
                }
            }
            catch (Exception ex)
            {
                InternalLogger.Error("Error when writing to RavenDB {0}", ex);

                throw;
            }
        }

        private dynamic CreateDocument(LogEventInfo logEvent)
        {
            dynamic document = new RavenDataBag(); //new ExpandoObject();
            //IDictionary<string, object> myUnderlyingObject = document;

            if (IncludeDefaults || Fields.Count == 0)
                AddDefaults(document, logEvent);

            // extra fields
            for (int i = 0; i < Fields.Count; ++i)
            {
                var value = GetValue(Fields[i], logEvent);
                if (value != null)
                    document[Fields[i].Name] = value;
            }

            AddProperties(document, logEvent);

            return document;
        }

        private void AddDefaults(RavenDataBag document, LogEventInfo logEvent)
        {
            document["Date"] = logEvent.TimeStamp;

            if (logEvent.Level != null)
                document["Level"] = logEvent.Level.Name;

            if (logEvent.LoggerName != null)
                document["Logger"] = logEvent.LoggerName;

            if (logEvent.FormattedMessage != null)
                document["Message"] = logEvent.FormattedMessage;

            if (logEvent.Exception != null)
                document["Exception"] = CreateException(logEvent.Exception);
        }

        private void AddProperties(RavenDataBag document, LogEventInfo logEvent)
        {
            if (logEvent.HasProperties || Properties.Count > 0)
            {
                dynamic propertiesDocument = new RavenDataBag(); //new ExpandoObject();
                //IDictionary<string, object> myUnderlyingObject = propertiesDocument;

                for (int i = 0; i < Properties.Count; ++i)
                {
                    string key = Properties[i].Name;
                    var value = GetValue(Properties[i], logEvent);

                    if (value != null)
                        propertiesDocument[key] = value;
                }

                if (IncludeEventProperties && logEvent.HasProperties)
                {
                    foreach (var property in logEvent.Properties)
                    {
                        if (property.Key == null || property.Value == null)
                            continue;

                        string key = Convert.ToString(property.Key, CultureInfo.InvariantCulture);
                        if (string.IsNullOrEmpty(key))
                            continue;

                        string value = Convert.ToString(property.Value, CultureInfo.InvariantCulture);
                        if (string.IsNullOrEmpty(value))
                            continue;

                        if (key.IndexOf('.') >= 0)
                            key = key.Replace('.', '_');

                        propertiesDocument[key] = value;
                    }
                }

                //if (myUnderlyingObject.Count > 0)
                document["Properties"] = propertiesDocument;
            }
        }

        private dynamic CreateException(Exception exception)
        {
            if (exception == null)
                return null;

            dynamic document = new RavenDataBag(); //new ExpandoObject();
            //IDictionary<string, object> myUnderlyingObject = document;
            document["Message"] = exception.Message;
            document["BaseMessage"] = exception.GetBaseException().Message;
            document["Text"] = exception.ToString();
            document["Type"] = exception.GetType().ToString();

#if !NETSTANDARD1_5
            if (exception is ExternalException external)
                document["ErrorCode"] = external.ErrorCode;
#endif
            document["HResult"] = exception.HResult;
            document["Source"] = exception.Source;

#if !NETSTANDARD1_5
            var method = exception.TargetSite;
            if (method != null)
            {
                document["MethodName"] = method.Name;

                AssemblyName assembly = method.Module.Assembly.GetName();
                document["ModuleName"] = assembly.Name;
                document["ModuleVersion"] = assembly.Version.ToString();
            }
#endif

            return document;
        }


        private object GetValue(RavenField field, LogEventInfo logEvent)
        {
            var value = (field.Layout != null ? RenderLogEvent(field.Layout, logEvent) : string.Empty).Trim();
            return value;
            /*if (string.IsNullOrEmpty(value))
                return null;

            if (string.Equals(field.BsonType, "String", StringComparison.OrdinalIgnoreCase))
                return new BsonString(value);

            BsonValue bsonValue;
            if (string.Equals(field.BsonType, "Boolean", StringComparison.OrdinalIgnoreCase)
                && MongoConvert.TryBoolean(value, out bsonValue))
                return bsonValue;

            if (string.Equals(field.BsonType, "DateTime", StringComparison.OrdinalIgnoreCase)
                && MongoConvert.TryDateTime(value, out bsonValue))
                return bsonValue;

            if (string.Equals(field.BsonType, "Double", StringComparison.OrdinalIgnoreCase)
                && MongoConvert.TryDouble(value, out bsonValue))
                return bsonValue;

            if (string.Equals(field.BsonType, "Int32", StringComparison.OrdinalIgnoreCase)
                && MongoConvert.TryInt32(value, out bsonValue))
                return bsonValue;

            if (string.Equals(field.BsonType, "Int64", StringComparison.OrdinalIgnoreCase)
                && MongoConvert.TryInt64(value, out bsonValue))
                return bsonValue;

            return new BsonString(value);*/
        }

        /*private IMongoCollection<BsonDocument> GetCollection()
        {
            string connectionString = _serverAddress != null ? RenderLogEvent(_serverAddress, _defaultLogEvent) : string.Empty;
            string collectionName = _collectionName != null ? RenderLogEvent(_collectionName, _defaultLogEvent) : string.Empty;
            string databaseName = _databaseName != null ? RenderLogEvent(_databaseName, _defaultLogEvent) : string.Empty;

            if (string.IsNullOrEmpty(connectionString))
                throw new NLogConfigurationException("Can not resolve RavenDB ConnectionString. Please make sure the ConnectionString property is set.");

            // cache mongo collection based on target name.
            var key = new RavenConnectionKey(connectionString, collectionName, databaseName);
            if (_collectionCache.TryGetValue(key, out var mongoCollection))
                return mongoCollection;

            return _collectionCache.GetOrAdd(key, k =>
            {
                // create collection
                var mongoUrl = new MongoUrl(connectionString);

                databaseName = !string.IsNullOrEmpty(databaseName) ? databaseName : (mongoUrl.DatabaseName ?? "NLog");
                collectionName = !string.IsNullOrEmpty(collectionName) ? collectionName : "Log";
                InternalLogger.Info("Connecting to RavenDB collection {0} in database {1}", collectionName, databaseName);

                var client = new MongoClient(mongoUrl);

                // Database name overrides connection string
                var database = client.GetDatabase(databaseName);

                if (CappedCollectionSize.HasValue)
                {
                    InternalLogger.Debug("Checking for existing RavenDB collection {0} in database {1}", collectionName, databaseName);
                    
                    var filterOptions = new ListCollectionNamesOptions { Filter = new BsonDocument("name", collectionName) };
                    if (!database.ListCollectionNames(filterOptions).Any())
                    {
                        InternalLogger.Debug("Creating new RavenDB collection {0} in database {1}", collectionName, databaseName);

                        // create capped
                        var options = new CreateCollectionOptions
                        {
                            Capped = true,
                            MaxSize = CappedCollectionSize,
                            MaxDocuments = CappedCollectionMaxItems
                        };

                        database.CreateCollection(collectionName, options);
                    }
                }

                var collection = database.GetCollection<BsonDocument>(collectionName);
                InternalLogger.Debug("Retrieved RavenDB collection {0} from database {1}", collectionName, databaseName);
                return collection;
            });
        }*/


        /*private static string GetConnectionString(string connectionName)
        {
            if (connectionName == null)
                throw new ArgumentNullException(nameof(connectionName));

#if NETSTANDARD1_5 || NETSTANDARD2_0
            return null;
#else
            var settings = System.Configuration.ConfigurationManager.ConnectionStrings[connectionName];
            if (settings == null)
                throw new NLogConfigurationException($"No connection string named '{connectionName}' could be found in the application configuration file.");

            string connectionString = settings.ConnectionString;
            if (string.IsNullOrEmpty(connectionString))
                throw new NLogConfigurationException($"The connection string '{connectionName}' in the application's configuration file does not contain the required connectionString attribute.");

            return connectionString;
#endif
        }*/
    }
}
