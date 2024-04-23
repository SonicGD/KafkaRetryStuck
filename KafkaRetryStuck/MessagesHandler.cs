using KafkaFlow;

namespace KafkaRetryStuck;

class MessagesHandler : IMessageHandler<TestMessage>
{
    public async Task Handle(IMessageContext context, TestMessage message)
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        if (message.Id == TestMessage.BadId)
        {
            throw new InvalidOperationException("BAD MESSAGE");
        }
    }
}