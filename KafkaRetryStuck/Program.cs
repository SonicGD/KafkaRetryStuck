using Confluent.Kafka;
using KafkaFlow;
using KafkaFlow.Consumers.DistributionStrategies;
using KafkaFlow.Producers;
using KafkaFlow.Retry;
using KafkaFlow.Serializer;
using KafkaRetryStuck;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AutoOffsetReset = KafkaFlow.AutoOffsetReset;


var instance = -1;
var init = true;
if (args.Length > 0 && int.TryParse(args[0], out var instanceNum))
{
    init = false;
    instance = instanceNum;
}

var topic = "test-topic";

var builder = Host.CreateDefaultBuilder();
builder.ConfigureLogging(loggingBuilder =>
{
    loggingBuilder.AddSimpleConsole(options =>
    {
        options.IncludeScopes = true;
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
});
builder.ConfigureServices(collection =>
{
    collection.AddKafka(configurationBuilder =>
    {
        configurationBuilder
            .UseMicrosoftLog()
            .AddCluster(cluster =>
            {
                cluster.WithBrokers(new[] { "127.0.0.1:9092" });
                cluster.CreateTopicIfNotExists(topic, 24, 1);
                cluster.AddProducer("default",
                    producer =>
                    {
                        producer.DefaultTopic(topic);
                        producer.AddMiddlewares(middlewares =>
                        {
                            middlewares.AddSerializer<NewtonsoftJsonSerializer>();
                        });
                    });
                if (!init)
                    cluster.AddConsumer(consumer =>
                    {
                        var name = $"consumer-{instance}";
                        consumer.WithName(name);
                        consumer.WithGroupId("consumers");
                        consumer.WithWorkersCount(10);
                        consumer.Topic(topic);
                        consumer.WithBufferSize(10);
                        consumer.WithAutoOffsetReset(AutoOffsetReset.Earliest);
                        consumer.WithWorkerDistributionStrategy<BytesSumDistributionStrategy>();
                        consumer.WithMaxPollIntervalMs((int)TimeSpan.FromMinutes(5).TotalMilliseconds);
                        var consumerConfig = new ConsumerConfig
                        {
                            MaxPartitionFetchBytes = 5 * 1024 * 1024,
                            ClientId = name,
                            SessionTimeoutMs = (int)TimeSpan.FromSeconds(15).TotalMilliseconds,
                            PartitionAssignmentStrategy = PartitionAssignmentStrategy.RoundRobin
                        };

                        consumer.WithConsumerConfig(consumerConfig);
                        consumer.AddMiddlewares(middlewares =>
                        {
                            middlewares.RetryForever(definitionBuilder => definitionBuilder.HandleAnyException()
                                .WithTimeBetweenTriesPlan(tryIteration => tryIteration switch
                                {
                                    1 => TimeSpan.FromSeconds(1),
                                    2 => TimeSpan.FromSeconds(30),
                                    _ => TimeSpan.FromMinutes(2)
                                }));
                            middlewares.AddDeserializer<NewtonsoftJsonDeserializer>();
                            middlewares.AddTypedHandlers(handlerConfigurationBuilder =>
                            {
                                handlerConfigurationBuilder.AddHandler<MessagesHandler>();
                            });
                        });
                    });
            });
    });
});
var app = builder.Build();
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var bus = app.Services.CreateKafkaBus();
await bus.StartAsync();

if (init)
{
    logger.LogInformation("Produce messages");
    var producer = app.Services.GetRequiredService<IProducerAccessor>().GetProducer("default");

    // Send some good messages
    var messages = Enumerable.Range(0, 200).Select(_ => new TestMessage
    {
        Id = Guid.NewGuid().ToString()
    });
    foreach (var message in messages) await producer.ProduceAsync(message.Id, message);

    // Send bad message
    await producer.ProduceAsync(TestMessage.BadId,
        new TestMessage
        {
            Id = TestMessage.BadId
        }
    );

    // Send more good message after
    messages = Enumerable.Range(0, 50).Select(_ => new TestMessage
    {
        Id = Guid.NewGuid().ToString()
    });
    foreach (var message in messages) await producer.ProduceAsync(message.Id, message);
}
else
{
    logger.LogInformation("Start consumers");
    await app.RunAsync();
}