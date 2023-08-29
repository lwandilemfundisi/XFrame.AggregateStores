using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XFrame.AggregateEventPublisher;
using XFrame.Aggregates;
using XFrame.Aggregates.Events;
using XFrame.Aggregates.Events.Serializers;
using XFrame.Aggregates.ExecutionResults;
using XFrame.AggregateStores.Exceptions;
using XFrame.Common;
using XFrame.Common.Extensions;
using XFrame.Ids;
using XFrame.Persistence;
using XFrame.Persistence.Extensions;
using XFrame.Persistence.Queries.Filterings;
using XFrame.Resilience;
using XFrame.Resilience.Extensions;

namespace XFrame.AggregateStores
{
    public class AggregateStore : IAggregateStore
    {
        private bool _aggregateExists;
        private readonly ILogger<AggregateStore> _logger;        
        private readonly IServiceProvider _serviceProvider;
        private readonly IEventJsonSerializer _eventJsonSerializer;
        private readonly IPersistenceFactory _persistenceFactory;
        private readonly IAggregateFactory _aggregateFactory;
        private readonly ITransientFaultHandler<IOptimisticConcurrencyResilientStrategy> _transientFaultHandler;
        private readonly ICancellationConfiguration _cancellationConfiguration;

        public AggregateStore(IServiceProvider serviceProvider)
        {
            _aggregateExists = true;
            _serviceProvider = serviceProvider;
            _logger = serviceProvider.GetRequiredService<ILogger<AggregateStore>>();
            _eventJsonSerializer = serviceProvider.GetRequiredService<IEventJsonSerializer>();
            _persistenceFactory = serviceProvider.GetRequiredService<IPersistenceFactory>();
            _aggregateFactory = serviceProvider.GetRequiredService<IAggregateFactory>();
            _transientFaultHandler = serviceProvider.GetRequiredService<ITransientFaultHandler<IOptimisticConcurrencyResilientStrategy>>();
            _cancellationConfiguration = serviceProvider.GetRequiredService<ICancellationConfiguration>();
        }

        public async Task<TAggregate> LoadAsync<TAggregate, TIdentity>(
            TIdentity id, 
            CancellationToken cancellationToken)
            where TAggregate : class, IAggregateRoot<TIdentity>
            where TIdentity : IIdentity
        {
            TAggregate aggregate = default;

            var criteria = new DomainCriteria();
            criteria.SafeAnd(new EqualityFilter("Id", id));
            IPersistence aggregatePersistence = _persistenceFactory.GetPersistence<TAggregate>();
            aggregate = await aggregatePersistence.Get<TAggregate, DomainCriteria>(criteria, CancellationToken.None);

            if(aggregate.IsNull())
            {
                _aggregateExists = false;
                aggregate = await _aggregateFactory.CreateNewAggregateAsync<TAggregate, TIdentity>(id).ConfigureAwait(false);
            }

            return aggregate;
        }

        public async Task StoreAsync<TAggregate, TIdentity>(
            TAggregate aggregate, 
            ISourceId sourceId, 
            CancellationToken cancellationToken)
            where TAggregate : class, IAggregateRoot<TIdentity>
            where TIdentity : IIdentity
        {
            await CommitAsync<TAggregate, TIdentity>(
                aggregate,
                _eventJsonSerializer,
                sourceId,
                cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task UpdateAsync<TAggregate, TIdentity>(
            TIdentity id, 
            ISourceId sourceId, 
            Func<TAggregate, CancellationToken, Task> updateAggregate, 
            CancellationToken cancellationToken)
            where TAggregate : class, IAggregateRoot<TIdentity>
            where TIdentity : IIdentity
        {
            var aggregateUpdateResult = await UpdateAsync<TAggregate, TIdentity, IExecutionResult>(
                id,
                sourceId,
                async (a, c) =>
                {
                    await updateAggregate(a, c).ConfigureAwait(false);
                    return ExecutionResult.Success();
                },
                cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<IAggregateUpdateResult<TExecutionResult>> UpdateAsync<TAggregate, TIdentity, TExecutionResult>(
            TIdentity id, 
            ISourceId sourceId, 
            Func<TAggregate, CancellationToken, Task<TExecutionResult>> updateAggregate, 
            CancellationToken cancellationToken)
            where TAggregate : class, IAggregateRoot<TIdentity>
            where TIdentity : IIdentity
            where TExecutionResult : IExecutionResult
        {
            var aggregateUpdateResult = await _transientFaultHandler.TryAsync(
                async c =>
                {
                    var aggregate = await LoadAsync<TAggregate, TIdentity>(id, c).ConfigureAwait(false);
                    if (aggregate.HasSourceId(sourceId))
                    {
                        throw new DuplicateOperationException(
                            sourceId,
                            id,
                            $"Aggregate '{typeof(TAggregate).PrettyPrint()}' has already had operation '{sourceId}' performed");
                    }

                    cancellationToken = _cancellationConfiguration.Limit(cancellationToken, CancellationBoundary.BeforeUpdatingAggregate);

                    var result = await updateAggregate(aggregate, c).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        _logger.LogDebug(
                            "Execution failed on aggregate {AggregateType}, disregarding any events emitted",
                            typeof(TAggregate).PrettyPrint());
                        return new AggregateUpdateResult<TExecutionResult>(result, null);
                    }

                    cancellationToken = _cancellationConfiguration.Limit(cancellationToken, CancellationBoundary.BeforeCommittingEvents);

                    var domainEvents = await CommitAsync<TAggregate, TIdentity>(
                        aggregate,
                        _eventJsonSerializer,
                        sourceId,
                        cancellationToken)
                        .ConfigureAwait(false);

                    return new AggregateUpdateResult<TExecutionResult>(result, domainEvents);
                },
                Label.Named("aggregate-update"),
                cancellationToken)
                .ConfigureAwait(false);

            if (aggregateUpdateResult.Result.IsSuccess &&
                aggregateUpdateResult.DomainEvents.Any())
            {
                var domainEventPublisher = _serviceProvider.GetRequiredService<IDomainEventPublisher>();
                await domainEventPublisher.PublishAsync(
                    aggregateUpdateResult.DomainEvents,
                    cancellationToken)
                    .ConfigureAwait(false);
            }

            return aggregateUpdateResult;
        }

        public async Task<IReadOnlyCollection<IDomainEvent>> CommitAsync<TAggregate, TIdentity>(
            TAggregate aggregate,
            IEventJsonSerializer _eventJsonSerializer,
            ISourceId sourceId,
            CancellationToken cancellationToken)
            where TAggregate : class, IAggregateRoot<TIdentity>
            where TIdentity : IIdentity

        {
            IPersistence aggregatePersistence = _persistenceFactory.GetPersistence<TAggregate>();

            if (!_aggregateExists)
                await aggregatePersistence.Save(this, CancellationToken.None);
            else
                await aggregatePersistence.Update(this, CancellationToken.None);

            if (aggregate.OccuredEvents.HasItems())
            {
                var domainEvents = aggregate.OccuredEvents
                .Select(e =>
                {
                    return _eventJsonSerializer.Serialize(e.AggregateEvent, e.Metadata);
                })
                .Select((e, i) =>
                {
                    var committedDomainEvent = new CommittedDomainEvent
                    {
                        AggregateId = aggregate.Id.Value,
                        AggregateName = e.Metadata[MetadataKeys.AggregateName],
                        AggregateSequenceNumber = e.AggregateSequenceNumber,
                        Data = e.SerializedData,
                        Metadata = e.SerializedMetadata,
                        GlobalSequenceNumber = i + 1,
                    };
                    return committedDomainEvent;
                })
                .Select(e => _eventJsonSerializer.Deserialize<TAggregate, TIdentity>(aggregate.Id, e))
                .ToList();

                ((IList<OccuredEvent>)aggregate.OccuredEvents).Clear();

                return domainEvents;
            }
            else
            {
                ((IList<OccuredEvent>)aggregate.OccuredEvents).Clear();
                return new IDomainEvent<TAggregate, TIdentity>[] { };
            }
        }
    }
}
