# Outbox Pattern Documentation

## Overview

ForqStudio uses the **Transactional Outbox Pattern** to ensure reliable, at-least-once delivery of domain events without distributed transactions. When a domain operation saves changes, domain events are persisted as outbox messages **in the same database transaction** as the business data. A Quartz.NET background job then picks them up and publishes them via MediatR.

This pattern solves the dual-write problem: without it, you risk saving the entity but failing to publish the event, or publishing the event before the data is committed.

---

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Domain Events](#domain-events)
3. [Outbox Message Entity](#outbox-message-entity)
4. [Converting Events to Outbox Messages](#converting-events-to-outbox-messages)
5. [Database Schema](#database-schema)
6. [Processing Outbox Messages](#processing-outbox-messages)
7. [Domain Event Handlers](#domain-event-handlers)
8. [Job Scheduling and Configuration](#job-scheduling-and-configuration)
9. [Error Handling and Idempotency](#error-handling-and-idempotency)
10. [End-to-End Flow](#end-to-end-flow)

---

## Architecture Overview

```
┌──────────────────────────────────────────────────────────┐
│                     Command Handler                      │
│   1. Load aggregate from repository                      │
│   2. Call domain method → raises domain event            │
│   3. Save changes (UnitOfWork.SaveChangesAsync)          │
└─────────────────────────┬────────────────────────────────┘
                          │
                          ▼
┌──────────────────────────────────────────────────────────┐
│              ApplicationDbContext.SaveChangesAsync       │
│   Intercepts save, reads domain events from entities,    │
│   serializes them as OutboxMessages, saves both in       │
│   a single atomic transaction                            │
└─────────────────────────┬────────────────────────────────┘
                          │  (same DB transaction)
                          ▼
                  ┌───────────────┐
                  │ outbox_messages│
                  │   (PostgreSQL) │
                  └───────┬───────┘
                          │
                          │  (Quartz.NET polls every N seconds)
                          ▼
┌──────────────────────────────────────────────────────────┐
│              ProcessOutboxMessagesJob                    │
│   1. SELECT unprocessed messages (FOR UPDATE)            │
│   2. Deserialize JSON → IDomainEvent                     │
│   3. publisher.Publish(domainEvent) via MediatR          │
│   4. UPDATE processed_on_utc / error                     │
└─────────────────────────┬────────────────────────────────┘
                          │
                          ▼
┌──────────────────────────────────────────────────────────┐
│              INotificationHandler<TDomainEvent>          │
│   Side effects: send emails, update read models, etc.    │
└──────────────────────────────────────────────────────────┘
```

---

## Domain Events

### Interface

Domain events implement `IDomainEvent`, which extends MediatR's `INotification`. This allows MediatR to publish them to registered handlers.

```csharp
// src/ForqStudio.Domain/Abstractions/IDomainEvent.cs
using MediatR;

namespace ForqStudio.Domain.Abstractions;

public interface IDomainEvent : INotification
{
}
```

### Declaring a Domain Event

Domain events are immutable records named in the past tense. They carry only the minimum data needed to identify what happened — typically just the aggregate ID, since handlers can reload the full aggregate from the database.

```csharp
// src/ForqStudio.Domain/Bookings/Events/BookingReservedDomainEvent.cs
public sealed record BookingReservedDomainEvent(Guid BookingId) : IDomainEvent;

// src/ForqStudio.Domain/Bookings/Events/BookingConfirmedDomainEvent.cs
public sealed record BookingConfirmedDomainEvent(Guid BookingId) : IDomainEvent;

// src/ForqStudio.Domain/Bookings/Events/BookingRejectedDomainEvent.cs
public sealed record BookingRejectedDomainEvent(Guid BookingId) : IDomainEvent;

// src/ForqStudio.Domain/Bookings/Events/BookingCancelledDomainEvent.cs
public sealed record BookingCancelledDomainEvent(Guid BookingId) : IDomainEvent;

// src/ForqStudio.Domain/Bookings/Events/BookingCompletedDomainEvent.cs
public sealed record BookingCompletedDomainEvent(Guid BookingId) : IDomainEvent;
```

### Raising Domain Events

The `Entity` base class holds a private list of pending domain events. Aggregates call `RaiseDomainEvent()` during state transitions. The events are not published immediately — they queue up until `SaveChangesAsync` is called.

```csharp
// src/ForqStudio.Domain/Abstractions/Entity.cs
public abstract class Entity
{
    private readonly List<IDomainEvent> _domainEvents = new();

    protected Entity(Guid id) { Id = id; }
    protected Entity() { }

    public Guid Id { get; private set; }

    public IReadOnlyList<IDomainEvent> DomainEvents() => _domainEvents.ToList();

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void RaiseDomainEvent(IDomainEvent domainEvent) =>
        _domainEvents.Add(domainEvent);
}
```

Inside a domain method, raising an event is a single line after updating state:

```csharp
// src/ForqStudio.Domain/Bookings/Booking.cs
public Result Confirm(DateTime utcNow)
{
    if (Status != BookingStatus.Reserved)
        return Result.Failure(BookingErrors.NotReserved);

    Status = BookingStatus.Confirmed;
    ConfirmedOn = utcNow;

    RaiseDomainEvent(new BookingConfirmedDomainEvent(Id)); // queued, not yet published

    return Result.Success();
}

public Result Cancel(DateTime utcNow)
{
    if (Status != BookingStatus.Reserved)
        return Result.Failure(BookingErrors.NotReserved);

    var currentDate = DateOnly.FromDateTime(utcNow);
    if (currentDate >= Duration.Start)
        return Result.Failure(BookingErrors.AlreadyStarted);

    Status = BookingStatus.Cancelled;
    CancelledOn = utcNow;

    RaiseDomainEvent(new BookingCancelledDomainEvent(Id));

    return Result.Success();
}
```

A single domain operation can raise multiple events. All of them will be captured and persisted atomically.

---

## Outbox Message Entity

`OutboxMessage` is a plain data record stored in the database. It represents a single domain event that needs to be (or has been) published.

```csharp
// src/ForqStudio.Infrastructure/Outbox/OutboxMessage.cs
public sealed class OutboxMessage
{
    public OutboxMessage(Guid id, DateTime occurredOnUtc, string type, string content)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
        Content = content;
        Type = type;
    }

    public Guid Id { get; init; }

    public DateTime OccurredOnUtc { get; init; }

    // The event's CLR type name (e.g., "BookingReservedDomainEvent")
    public string Type { get; init; }

    // JSON-serialized domain event, including type metadata for polymorphic deserialization
    public string Content { get; init; }

    // Null until the job processes this message
    public DateTime? ProcessedOnUtc { get; init; }

    // Populated if an exception occurred during processing
    public string? Error { get; init; }
}
```

| Column | Purpose |
|---|---|
| `id` | Unique identifier for the message |
| `occurred_on_utc` | When the domain event was raised (ordering for processing) |
| `type` | CLR type name, useful for debugging and filtering |
| `content` | Full JSON payload with type metadata for deserialization |
| `processed_on_utc` | Set by the job on success or failure — `NULL` means unprocessed |
| `error` | Exception string if processing failed; allows post-mortem inspection |

---

## Converting Events to Outbox Messages

`ApplicationDbContext.SaveChangesAsync` intercepts every save. Before delegating to EF Core, it scans the change tracker for entities with pending domain events, serializes each event into an `OutboxMessage`, clears the events from the entity, and adds the messages to the same unit of work.

```csharp
// src/ForqStudio.Infrastructure/ApplicationDbContext.cs
public sealed class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options,
    IDateTimeProvider _dateTimeProvider
) : DbContext(options), IUnitOfWork
{
    private static readonly JsonSerializerSettings JsonSerializerSettings = new()
    {
        TypeNameHandling = TypeNameHandling.All  // embeds "$type" in JSON for polymorphic deserialization
    };

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            AddDomainEventsAsOutboxMessages(); // must run before base.SaveChangesAsync

            var result = await base.SaveChangesAsync(cancellationToken);

            return result;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyException("Concurrency exception occurred.", ex);
        }
    }

    private void AddDomainEventsAsOutboxMessages()
    {
        var outboxMessages = ChangeTracker
            .Entries<Entity>()               // find all tracked entities that inherit Entity
            .Select(entry => entry.Entity)
            .SelectMany(entity =>
            {
                var domainEvents = entity.DomainEvents();
                entity.ClearDomainEvents();  // prevent double-publishing if SaveChanges is called again
                return domainEvents;
            })
            .Select(domainEvent => new OutboxMessage(
                Guid.NewGuid(),
                _dateTimeProvider.UtcNow,
                domainEvent.GetType().Name,
                JsonConvert.SerializeObject(domainEvent, JsonSerializerSettings)))
            .ToList();

        AddRange(outboxMessages);            // tracked by EF Core, saved in the same transaction
    }
}
```

**Key details:**

- `TypeNameHandling.All` embeds a `$type` field in the JSON so `JsonConvert.DeserializeObject<IDomainEvent>` can reconstruct the correct concrete type later.
- `ClearDomainEvents()` is called before the events are processed to prevent the same event from being persisted twice if `SaveChangesAsync` is called multiple times in the same request.
- Because `AddRange(outboxMessages)` runs before `base.SaveChangesAsync`, the entity changes and the outbox messages are written in **one atomic database transaction**. Either both succeed or both roll back.

### What the serialized JSON looks like

For a `BookingReservedDomainEvent(BookingId: "abc-123")`, the stored `content` column will contain:

```json
{
  "$type": "ForqStudio.Domain.Bookings.Events.BookingReservedDomainEvent, ForqStudio.Domain",
  "BookingId": "abc-123-..."
}
```

The `$type` field is what allows the job to deserialize back to the concrete `IDomainEvent` type without knowing the type at compile time.

---

## Database Schema

The `outbox_messages` table is created by a dedicated migration:

```csharp
// src/ForqStudio.Infrastructure/Migrations/20240514133129_Add_Outbox.cs
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.CreateTable(
        name: "outbox_messages",
        columns: table => new
        {
            id               = table.Column<Guid>(type: "uuid", nullable: false),
            occurred_on_utc  = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            type             = table.Column<string>(type: "text", nullable: false),
            content          = table.Column<string>(type: "jsonb", nullable: false), // PostgreSQL native JSON
            processed_on_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            error            = table.Column<string>(type: "text", nullable: true)
        },
        constraints: table =>
        {
            table.PrimaryKey("pk_outbox_messages", x => x.id);
        });
}
```

The `content` column uses PostgreSQL's `jsonb` type (configured in `OutboxMessageConfiguration`), which stores JSON in a binary format that enables efficient querying and indexing if needed.

```csharp
// src/ForqStudio.Infrastructure/Configurations/OutboxMessageConfiguration.cs
internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(outboxMessage => outboxMessage.Id);
        builder.Property(outboxMessage => outboxMessage.Content).HasColumnType("jsonb");
    }
}
```

---

## Processing Outbox Messages

`ProcessOutboxMessagesJob` is a Quartz.NET job that polls the outbox table on a configurable interval. It fetches a batch of unprocessed messages, deserializes each one back into a domain event, and publishes it via MediatR.

```csharp
// src/ForqStudio.Infrastructure/Outbox/ProcessOutboxMessagesJob.cs
[DisallowConcurrentExecution] // prevents overlapping job executions
internal sealed class ProcessOutboxMessagesJob(
    ISqlConnectionFactory sqlConnectionFactory,
    IPublisher publisher,
    IDateTimeProvider dateTimeProvider,
    IOptions<OutboxOptions> outboxOptions,
    ILogger<ProcessOutboxMessagesJob> logger
) : IJob
{
    private static readonly JsonSerializerSettings JsonSerializerSettings = new()
    {
        TypeNameHandling = TypeNameHandling.All
    };

    private readonly OutboxOptions outboxOptions = outboxOptions.Value;

    public async Task Execute(IJobExecutionContext context)
    {
        logger.LogInformation("Beginning to process outbox messages");

        using var connection = sqlConnectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();

        var outboxMessages = await GetOutboxMessagesAsync(connection, transaction);

        foreach (var outboxMessage in outboxMessages)
        {
            Exception? exception = null;

            try
            {
                var domainEvent = JsonConvert.DeserializeObject<IDomainEvent>(
                    outboxMessage.Content,
                    JsonSerializerSettings)!;

                await publisher.Publish(domainEvent, context.CancellationToken);
            }
            catch (Exception caughtException)
            {
                logger.LogError(
                    caughtException,
                    "Exception while processing outbox message {MessageId}",
                    outboxMessage.Id);

                exception = caughtException;
            }

            await UpdateOutboxMessageAsync(connection, transaction, outboxMessage, exception);
        }

        transaction.Commit();

        logger.LogInformation("Completed processing outbox messages");
    }
}
```

### Fetching messages

Messages are fetched in order of `occurred_on_utc` (oldest first) up to `BatchSize`. The `FOR UPDATE` clause locks the selected rows for the duration of the transaction, preventing a second job instance from picking up the same messages if two were somehow running concurrently (the `[DisallowConcurrentExecution]` attribute is the primary guard, but the lock adds a database-level safety net).

```csharp
private async Task<IReadOnlyList<OutboxMessageResponse>> GetOutboxMessagesAsync(
    IDbConnection connection,
    IDbTransaction transaction)
{
    var sql = $"""
        SELECT id, content
        FROM outbox_messages
        WHERE processed_on_utc IS NULL
        ORDER BY occurred_on_utc
        LIMIT {outboxOptions.BatchSize}
        FOR UPDATE
        """;

    var outboxMessages = await connection.QueryAsync<OutboxMessageResponse>(sql, transaction: transaction);

    return outboxMessages.ToList();
}

internal sealed record OutboxMessageResponse(Guid Id, string Content);
```

### Marking messages as processed

After each message is handled (whether successfully or with an error), the row is updated with `processed_on_utc` and optionally `error`. A message is **always** marked as processed even when it fails — this prevents a single poisoned message from blocking all subsequent messages in the queue.

```csharp
private async Task UpdateOutboxMessageAsync(
    IDbConnection connection,
    IDbTransaction transaction,
    OutboxMessageResponse outboxMessage,
    Exception? exception)
{
    const string sql = @"
        UPDATE outbox_messages
        SET processed_on_utc = @ProcessedOnUtc,
            error = @Error
        WHERE id = @Id";

    await connection.ExecuteAsync(
        sql,
        new
        {
            outboxMessage.Id,
            ProcessedOnUtc = dateTimeProvider.UtcNow,
            Error = exception?.ToString()
        },
        transaction: transaction);
}
```

A failed message will have `processed_on_utc` set and a non-null `error`. It will **not** be retried automatically. Failed messages should be monitored and replayed manually if needed.

---

## Domain Event Handlers

Handlers are the final destination of a domain event. They implement MediatR's `INotificationHandler<TEvent>` and are registered automatically via assembly scanning. Each handler performs a single side effect: sending an email, updating a read model, triggering another workflow, etc.

```csharp
// src/ForqStudio.Application/Bookings/ReserveBooking/BookingReservedDomainHandler.cs
internal sealed record BookingReservedDomainHandler(
    IBookingRepository bookingRepository,
    IUserRepository userRepository,
    IEmailService emailService
) : INotificationHandler<BookingReservedDomainEvent>
{
    public async Task Handle(
        BookingReservedDomainEvent notification,
        CancellationToken cancellationToken)
    {
        var booking = await bookingRepository.GetByIdAsync(notification.BookingId, cancellationToken);
        if (booking is null)
            return;

        var user = await userRepository.GetByIdAsync(booking.UserId, cancellationToken);
        if (user is null)
            return;

        await emailService.SendEmailAsync(
            user.Email,
            "Booking Reserved",
            "Your booking has been reserved. You have 10 minutes to confirm your booking");
    }
}
```

**Handler guidelines:**

- Handlers live in the **Application** layer, not Domain or Infrastructure.
- They receive only the event ID — they re-query the database to get current state. This is intentional: by the time the handler runs (seconds later, via the background job), the state may have changed, and you want to act on the current state.
- A single domain event can have multiple handlers. MediatR publishes to all of them.
- Handlers should be idempotent where possible. Because the outbox guarantees at-least-once delivery, a message could theoretically be published more than once (e.g., if the job crashes after `publisher.Publish` but before `UpdateOutboxMessageAsync` commits).

---

## Job Scheduling and Configuration

### OutboxOptions

The polling interval and batch size are externally configurable:

```csharp
// src/ForqStudio.Infrastructure/Outbox/OutboxOptions.cs
public sealed class OutboxOptions
{
    public int IntervalInSeconds { get; init; }

    public int BatchSize { get; init; }
}
```

### appsettings

```json
// src/ForqStudio.Api/appsettings.Development.json
{
  "Outbox": {
    "IntervalInSeconds": 10,
    "BatchSize": 10
  }
}
```

### Quartz.NET Job Setup

`ProcessOutboxMessagesJobSetup` implements `IConfigureOptions<QuartzOptions>`, so it integrates with .NET's options pipeline. The job triggers on a simple repeating schedule driven by `IntervalInSeconds`.

```csharp
// src/ForqStudio.Infrastructure/Outbox/ProcessOutboxMessagesJobSetup.cs
public class ProcessOutboxMessagesJobSetup(IOptions<OutboxOptions> outboxOptions)
    : IConfigureOptions<QuartzOptions>
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
                        schedule
                            .WithIntervalInSeconds(outboxOptions.IntervalInSeconds)
                            .RepeatForever()));
    }
}
```

### DI Registration

Everything is wired up in `DependencyInjection.cs`:

```csharp
// src/ForqStudio.Infrastructure/DependencyInjection.cs
private static void AddBackgroundJobs(IServiceCollection services, IConfiguration configuration)
{
    services.Configure<OutboxOptions>(configuration.GetSection("Outbox"));

    services.AddQuartz(c =>
    {
        var scheduler = Guid.NewGuid();
        c.SchedulerId = $"default-id-{scheduler}";
        c.SchedulerName = $"default-name-{scheduler}";
    });

    services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

    services.ConfigureOptions<ProcessOutboxMessagesJobSetup>();
}
```

`WaitForJobsToComplete = true` ensures the hosted service waits for any in-flight job execution to finish before the application shuts down, preventing partial processing of a batch.

---

## Error Handling and Idempotency

### What happens when a handler throws

If `publisher.Publish` throws, the exception is caught, logged, and stored in the `error` column. The message is still marked with a `processed_on_utc` timestamp. The job continues to the next message — one bad message does not block the queue.

```
outbox_messages row after failure:
  processed_on_utc = 2026-03-29T10:00:05Z
  error            = "System.Net.Http.HttpRequestException: Connection refused..."
```

### At-least-once delivery

The pattern guarantees **at-least-once** delivery, not exactly-once. A crash between `publisher.Publish` succeeding and `UpdateOutboxMessageAsync` committing would leave the message with `processed_on_utc IS NULL`, causing it to be picked up again on the next job run. Design handlers to be idempotent (e.g., check whether the email was already sent, use upserts for read models).

### Poisoned messages

Messages that fail processing are not retried automatically. To retry a failed message, reset its `processed_on_utc` to `NULL` directly in the database. Monitor the `error` column to detect and alert on handler failures.

---

## End-to-End Flow

Here is the complete sequence for a `ReserveBooking` command:

**1. Command handler loads the aggregate and calls the domain method:**

```csharp
var apartment = await apartmentRepository.GetByIdAsync(command.ApartmentId, cancellationToken);
// ...
var booking = Booking.Reserve(apartment, command.UserId, duration, utcNow, pricingService);
// booking._domainEvents = [BookingReservedDomainEvent(booking.Id)]
```

**2. Handler saves via the unit of work:**

```csharp
bookingRepository.Add(booking);
await unitOfWork.SaveChangesAsync(cancellationToken);
```

**3. `SaveChangesAsync` intercepts, scans the change tracker:**

```
ChangeTracker.Entries<Entity>()
  → finds Booking entity with 1 pending event
  → serializes BookingReservedDomainEvent to JSON
  → creates OutboxMessage { Id, OccurredOnUtc, Type="BookingReservedDomainEvent", Content="{...}" }
  → clears Booking._domainEvents
  → AddRange([outboxMessage])
```

**4. EF Core writes to PostgreSQL in one transaction:**

```sql
INSERT INTO bookings (...) VALUES (...);
INSERT INTO outbox_messages (id, occurred_on_utc, type, content) VALUES (...);
-- COMMIT
```

**5. ~10 seconds later, Quartz.NET fires `ProcessOutboxMessagesJob`:**

```sql
SELECT id, content
FROM outbox_messages
WHERE processed_on_utc IS NULL
ORDER BY occurred_on_utc
LIMIT 10
FOR UPDATE;
```

**6. Job deserializes and publishes:**

```csharp
var domainEvent = JsonConvert.DeserializeObject<IDomainEvent>(outboxMessage.Content, settings);
// domainEvent is BookingReservedDomainEvent { BookingId = "..." }

await publisher.Publish(domainEvent, cancellationToken);
// MediatR routes to BookingReservedDomainHandler
```

**7. Handler executes the side effect:**

```csharp
// BookingReservedDomainHandler
var booking = await bookingRepository.GetByIdAsync(notification.BookingId, cancellationToken);
var user = await userRepository.GetByIdAsync(booking.UserId, cancellationToken);
await emailService.SendEmailAsync(user.Email, "Booking Reserved", "...");
```

**8. Job marks the message as processed:**

```sql
UPDATE outbox_messages
SET processed_on_utc = '2026-03-29T10:00:10Z',
    error = NULL
WHERE id = '...';
-- COMMIT
```

The booking is now reserved, persisted, and the confirmation email has been sent — all with guaranteed consistency between the business data and the side effect.
