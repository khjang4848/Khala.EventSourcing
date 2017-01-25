﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.RetryPolicies;
using Microsoft.WindowsAzure.Storage.Table;
using Moq;
using Ploeh.AutoFixture;
using Ploeh.AutoFixture.AutoMoq;
using Ploeh.AutoFixture.Idioms;
using ReactiveArchitecture.FakeDomain;
using ReactiveArchitecture.FakeDomain.Events;
using ReactiveArchitecture.Messaging;

namespace ReactiveArchitecture.EventSourcing.Azure
{
    [TestClass]
    public class AzureEventStore_features
    {
        private static CloudStorageAccount s_storageAccount;
        private static CloudTable s_eventTable;
        private static bool s_storageEmulatorConnected;
        private IFixture fixture;
        private IMessageSerializer serializer;
        private AzureEventStore sut;
        private Guid userId;

        public TestContext TestContext { get; set; }

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            try
            {
                s_storageAccount = CloudStorageAccount.DevelopmentStorageAccount;
                CloudTableClient tableClient = s_storageAccount.CreateCloudTableClient();
                s_eventTable = tableClient.GetTableReference("AzureEventStoreTestEventStore");
                s_eventTable.DeleteIfExists(new TableRequestOptions { RetryPolicy = new NoRetry() });
                s_eventTable.Create();
                s_storageEmulatorConnected = true;
            }
            catch (StorageException exception)
            when (exception.InnerException is WebException)
            {
                context.WriteLine("{0}", exception);
            }
        }

        [TestInitialize]
        public void TestInitialize()
        {
            if (s_storageEmulatorConnected == false)
            {
                Assert.Inconclusive("Could not connect to Azure Storage Emulator. See the output for details. Refer to the following URL for more information: http://go.microsoft.com/fwlink/?LinkId=392237");
            }

            fixture = new Fixture().Customize(new AutoMoqCustomization());
            fixture.Inject(s_eventTable);
            serializer = new JsonMessageSerializer();
            sut = new AzureEventStore(s_eventTable, serializer);
            userId = Guid.NewGuid();
        }

        [TestMethod]
        public void sut_implements_IAzureEventStore()
        {
            sut.Should().BeAssignableTo<IAzureEventStore>();
        }

        [TestMethod]
        public void class_has_guard_clauses()
        {
            var assertion = new GuardClauseAssertion(fixture);
            assertion.Verify(typeof(AzureEventStore));
        }

        [TestMethod]
        public void SaveEvents_fails_if_events_contains_null()
        {
            var eventTable = new Mock<CloudTable>(s_eventTable.Uri).Object;
            var sut = new AzureEventStore(eventTable, serializer);
            var events = new IDomainEvent[]
            {
                new FakeUserCreated(),
                null
            };

            Func<Task> action = () => sut.SaveEvents<FakeUser>(events, CancellationToken.None);

            action.ShouldThrow<ArgumentException>()
                .Where(x => x.ParamName == "events");
            Mock.Get(eventTable).Verify(
                x => x.ExecuteBatchAsync(It.IsAny<TableBatchOperation>()),
                Times.Never());
        }

        [TestMethod]
        public async Task SaveEvents_inserts_pending_event_entities_correctly()
        {
            // Arrange
            var created = fixture.Create<FakeUserCreated>();
            var usernameChanged = fixture.Create<FakeUsernameChanged>();
            var events = new DomainEvent[] { created, usernameChanged };
            RaiseEvents(userId, events);

            // Act
            await sut.SaveEvents<FakeUser>(events, CancellationToken.None);

            // Assert
            string partitionKey = PendingEventTableEntity.GetPartitionKey(typeof(FakeUser), userId);
            var query = new TableQuery<PendingEventTableEntity>().Where($"PartitionKey eq '{partitionKey}'");
            IEnumerable<PendingEventTableEntity> pendingEvents = s_eventTable.ExecuteQuery(query);
            foreach (var t in pendingEvents.Zip(events, (pending, source) =>
                              new { Pending = pending, Source = source }))
            {
                var actual = new
                {
                    t.Pending.RowKey,
                    t.Pending.PersistedPartition,
                    t.Pending.Version,
                    Envelope = (Envelope)serializer.Deserialize(t.Pending.EnvelopeJson)
                };
                actual.ShouldBeEquivalentTo(new
                {
                    RowKey = PendingEventTableEntity.GetRowKey(t.Source.Version),
                    PersistedPartition = EventTableEntity.GetPartitionKey(typeof(FakeUser), userId),
                    t.Source.Version,
                    Envelope = new Envelope(t.Source)
                },
                opts => opts.Excluding(x => x.Envelope.MessageId));
            }
        }

        [TestMethod]
        public async Task SaveEvents_inserts_event_entities_correctly()
        {
            // Arrange
            var created = fixture.Create<FakeUserCreated>();
            var usernameChanged = fixture.Create<FakeUsernameChanged>();
            var events = new DomainEvent[] { created, usernameChanged };
            RaiseEvents(userId, events);

            // Act
            await sut.SaveEvents<FakeUser>(events, CancellationToken.None);

            // Assert
            string partitionKey = EventTableEntity.GetPartitionKey(typeof(FakeUser), userId);
            var query = new TableQuery<EventTableEntity>().Where($"PartitionKey eq '{partitionKey}'");
            IEnumerable<EventTableEntity> persistedEvents = s_eventTable.ExecuteQuery(query);
            foreach (var t in persistedEvents.Zip(events, (persisted, source) => new { Persisted = persisted, Source = source }))
            {
                var actual = new
                {
                    t.Persisted.RowKey,
                    t.Persisted.Version,
                    t.Persisted.EventType,
                    Event = (DomainEvent)serializer.Deserialize(t.Persisted.EventJson),
                    t.Persisted.RaisedAt
                };
                actual.ShouldBeEquivalentTo(new
                {
                    RowKey = EventTableEntity.GetRowKey(t.Source.Version),
                    t.Source.Version,
                    EventType = t.Source.GetType().FullName,
                    Event = t.Source,
                    t.Source.RaisedAt
                });
            }
        }

        [TestMethod]
        public async Task SaveEvents_executes_batch_twice()
        {
            var eventTable = new Mock<CloudTable>(s_eventTable.Uri).Object;
            var sut = new AzureEventStore(eventTable, serializer);
            var created = fixture.Create<FakeUserCreated>();
            var events = new DomainEvent[] { created };
            RaiseEvents(userId, events);

            await sut.SaveEvents<FakeUser>(events, CancellationToken.None);

            Mock.Get(eventTable).Verify(
                x =>
                x.ExecuteBatchAsync(
                    It.IsAny<TableBatchOperation>(),
                    CancellationToken.None),
                Times.Exactly(2));
        }

        [TestMethod]
        public void SaveEvents_does_not_insert_event_entities_if_fails_to_insert_pending_events()
        {
            // Arrange
            var eventTable = new Mock<CloudTable>(s_eventTable.Uri).Object;
            PropertyInfo entityProperty = typeof(TableOperation).GetProperty(
                "Entity",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Mock.Get(eventTable)
                .Setup(
                    x =>
                    x.ExecuteBatchAsync(
                        It.Is<TableBatchOperation>(
                            p =>
                            ((TableEntity)entityProperty.GetValue(p[0])).PartitionKey.StartsWith("PendingEvent")),
                        CancellationToken.None))
                .ThrowsAsync(new StorageException());

            var sut = new AzureEventStore(eventTable, serializer);

            var created = fixture.Create<FakeUserCreated>();
            var events = new DomainEvent[] { created };
            RaiseEvents(userId, events);

            // Act
            Func<Task> action = () => sut.SaveEvents<FakeUser>(events, CancellationToken.None);

            // Assert
            action.ShouldThrow<StorageException>();
            Mock.Get(eventTable)
                .Verify(
                    x =>
                    x.ExecuteBatchAsync(
                        It.Is<TableBatchOperation>(
                            p =>
                            ((TableEntity)entityProperty.GetValue(p[0])).PartitionKey.StartsWith("Event")),
                        It.IsAny<CancellationToken>()),
                    Times.Never());
        }

        [TestMethod]
        public void SaveEvents_does_not_fail_even_if_events_empty()
        {
            var events = new DomainEvent[] { };
            Func<Task> action = () => sut.SaveEvents<FakeUser>(events, CancellationToken.None);
            action.ShouldNotThrow();
        }

        [TestMethod]
        public async Task LoadEvents_restores_domain_events_correctly()
        {
            // Arrange
            var created = fixture.Create<FakeUserCreated>();
            var usernameChanged = fixture.Create<FakeUsernameChanged>();
            var events = new DomainEvent[] { created, usernameChanged };
            RaiseEvents(userId, events);
            await sut.SaveEvents<FakeUser>(events, CancellationToken.None);

            // Act
            IEnumerable<IDomainEvent> actual =
                await sut.LoadEvents<FakeUser>(userId, 0, CancellationToken.None);

            // Assert
            actual.Should().BeInAscendingOrder(e => e.Version);
            actual.ShouldAllBeEquivalentTo(events);
        }

        [TestMethod]
        public async Task LoadEvents_correctly_restores_domain_events_after_specified_version()
        {
            // Arrange
            var created = fixture.Create<FakeUserCreated>();
            var usernameChanged = fixture.Create<FakeUsernameChanged>();
            var events = new DomainEvent[] { created, usernameChanged };
            RaiseEvents(userId, events);
            await sut.SaveEvents<FakeUser>(events, CancellationToken.None);

            // Act
            IEnumerable<IDomainEvent> actual =
                await sut.LoadEvents<FakeUser>(userId, 1, CancellationToken.None);

            // Assert
            actual.Should().BeInAscendingOrder(e => e.Version);
            actual.ShouldAllBeEquivalentTo(events.Skip(1));
        }

        private void RaiseEvents(Guid sourceId, params DomainEvent[] events)
        {
            RaiseEvents(sourceId, 0, events);
        }

        private void RaiseEvents(
            Guid sourceId, int versionOffset, params DomainEvent[] events)
        {
            for (int i = 0; i < events.Length; i++)
            {
                events[i].SourceId = sourceId;
                events[i].Version = versionOffset + i + 1;
                events[i].RaisedAt = DateTimeOffset.Now;
            }
        }
    }
}
