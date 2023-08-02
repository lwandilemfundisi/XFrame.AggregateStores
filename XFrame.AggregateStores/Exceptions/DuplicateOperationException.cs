using XFrame.Ids;

namespace XFrame.AggregateStores.Exceptions
{
    public class DuplicateOperationException : Exception
    {
        public ISourceId SourceId { get; }
        public IIdentity AggregateId { get; }

        public DuplicateOperationException(
            ISourceId sourceId, IIdentity aggregateId, string message)
            : base(message)
        {
            SourceId = sourceId;
            AggregateId = aggregateId;
        }
    }
}
