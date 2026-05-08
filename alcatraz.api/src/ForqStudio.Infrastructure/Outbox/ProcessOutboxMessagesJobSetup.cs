using Microsoft.Extensions.Options;
using Quartz;

namespace ForqStudio.Infrastructure.Outbox;

public class ProcessOutboxMessagesJobSetup(IOptions<OutboxOptions> outboxOptions) : IConfigureOptions<QuartzOptions>
{
    private readonly OutboxOptions outboxOptions = outboxOptions.Value;
    public void Configure(QuartzOptions options)
    {
        const string jobName = nameof(ProcessOutboxMessagesJob);

        options
            .AddJob<ProcessOutboxMessagesJob>(configure => configure.WithIdentity(jobName))
            .AddTrigger(configure =>
                configure
                    .ForJob(jobName)
                    .WithSimpleSchedule(schedule =>
                        schedule.WithIntervalInSeconds(outboxOptions.IntervalInSeconds).RepeatForever()));
    }
}