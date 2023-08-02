using XFrame.Aggregates;
using XFrame.Aggregates.Events.Serializers;
using XFrame.Aggregates.Events;
using XFrame.Aggregates.ExecutionResults;
using XFrame.Ids;

namespace XFrame.AggregateStores
{
    public interface IAggregateStore
    {
        Task<TAggregate> LoadAsync<TAggregate, TIdentity>(
            TIdentity id,
            CancellationToken cancellationToken)
            where TAggregate : class, IAggregateRoot<TIdentity>
            where TIdentity : IIdentity;

        Task UpdateAsync<TAggregate, TIdentity>(
            TIdentity id,
            ISourceId sourceId,
            Func<TAggregate, CancellationToken, Task> updateAggregate,
            CancellationToken cancellationToken)
            where TAggregate : class, IAggregateRoot<TIdentity>
            where TIdentity : IIdentity;

        Task<IAggregateUpdateResult<TExecutionResult>> UpdateAsync<TAggregate, TIdentity, TExecutionResult>(
            TIdentity id,
            ISourceId sourceId,
            Func<TAggregate, CancellationToken, Task<TExecutionResult>> updateAggregate,
            CancellationToken cancellationToken)
            where TAggregate : class, IAggregateRoot<TIdentity>
            where TIdentity : IIdentity
            where TExecutionResult : IExecutionResult;

        Task StoreAsync<TAggregate, TIdentity>(
            TAggregate aggregate,
            ISourceId sourceId,
            CancellationToken cancellationToken)
            where TAggregate : class, IAggregateRoot<TIdentity>
            where TIdentity : IIdentity;

        Task<IReadOnlyCollection<IDomainEvent>> CommitAsync<TAggregate, TIdentity>(
            TAggregate aggregate,
            IEventJsonSerializer _eventJsonSerializer,
            ISourceId sourceId,
            CancellationToken cancellationToken)
            where TAggregate : class, IAggregateRoot<TIdentity>
            where TIdentity : IIdentity;
    }
}
