using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EventStore.ClientAPI;
using Newtonsoft.Json;

namespace Evento.Repository
{
    public class EventStoreDomainRepository : DomainRepositoryBase
    {
        public readonly string Category;
        private readonly IEventStoreConnection _connection;

        public EventStoreDomainRepository(string category, IEventStoreConnection connection) 
        {
            Category = category;
            _connection = connection;
        }

        private string AggregateToStreamName(Type type, string id)
        {
            return $"{Category}-{type.Name}-{id}";
        }

        public override TResult GetById<TResult>(string id) 
        {
            var streamName = AggregateToStreamName(typeof(TResult), id);
            var eventsSlice = _connection.ReadStreamEventsForwardAsync(streamName, 0, 4096, false);
            if (eventsSlice.Result.Status == SliceReadStatus.StreamNotFound)
            {
                throw new AggregateNotFoundException("Could not found aggregate of type " + typeof(TResult) + " and id " + id);
            }
            var deserializedEvents = eventsSlice.Result.Events.Select(e =>
            {
                var metadata = SerializationUtils.DeserializeObject<Dictionary<string, string>>(e.OriginalEvent.Metadata);
                var eventData = SerializationUtils.DeserializeObject(e.OriginalEvent.Data, metadata[SerializationUtils.EventClrTypeHeader]);
                return eventData as Event;
            });
            return BuildAggregate<TResult>(deserializedEvents);
        }

        private EventData CreateEventData(object @event, string correlationId)
        {
            var metadata = new Dictionary<string, string>()
            {
                {
                    SerializationUtils.EventClrTypeHeader, @event.GetType().AssemblyQualifiedName
                },
                {
                    "Domain", Category
                },
                {
                    "$correlationId", correlationId
                }
            };
            var eventDataHeaders = SerializeObject(metadata);
            var data = SerializeObject(@event);
            var eventData = new EventData(Guid.NewGuid(), @event.GetType().Name, true, data, eventDataHeaders);
            return eventData;
        }

        private static byte[] SerializeObject(object obj)
        {
            var jsonObj = JsonConvert.SerializeObject(obj);
            var data = Encoding.UTF8.GetBytes(jsonObj);
            return data;
        }

        public override IEnumerable<Event> Save<TAggregate>(TAggregate aggregate) 
        {
            // Synchronous save operation
            var streamName = AggregateToStreamName(aggregate.GetType(), aggregate.AggregateId);
            var events = aggregate.UncommitedEvents().ToList();
            var originalVersion = CalculateExpectedVersion(aggregate, events);
            var expectedVersion = originalVersion == 0 ? ExpectedVersion.NoStream : originalVersion - 1;
            var eventData = events.Select(@event => CreateEventData(@event, aggregate.AggregateId)).ToArray();
            try
            {
                if (events.Count > 0)
                    _connection.AppendToStreamAsync(streamName, expectedVersion, eventData).Wait();
            }
            catch (AggregateException)
            {
                // Try to save using ExpectedVersion.Any to swallow silently WrongExpectedVersion error
                _connection.AppendToStreamAsync(streamName, ExpectedVersion.Any, eventData).Wait();
            }
            aggregate.ClearUncommitedEvents();
            return events;
        }
    }
}