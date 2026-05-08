# ForqStudio - Clean Architecture Booking System

## Project Overview

ForqStudio is a booking management system for apartment/property rentals built with .NET 8 and Clean Architecture principles. The application provides a complete booking workflow including search, reservation, confirmation, rejection, cancellation, and completion with user reviews.

**Tech Stack:** .NET 8, ASP.NET Core Web API, PostgreSQL, Entity Framework Core, Redis, Keycloak, MediatR, Quartz.NET

**Architecture:** Clean Architecture with CQRS, DDD, and Result pattern

## Architecture Principles

### Core Principles

1. **Dependency Rule**: Dependencies point inward - Infrastructure and API depend on Application, Application depends on Domain. Domain has NO external dependencies.

2. **Layer Responsibilities**:
   - **Domain** (`ForqStudio.Domain`): Core business logic, aggregates, value objects, domain events, repository interfaces
   - **Application** (`ForqStudio.Application`): Use cases (commands/queries), application services, interfaces for infrastructure
   - **Infrastructure** (`ForqStudio.Infrastructure`): External integrations, data access, authentication, caching, email
   - **API** (`ForqStudio.Api`): REST endpoints, middleware, configuration

3. **CQRS Pattern**:
   - Commands for write operations (implement `ICommand<T>`)
   - Queries for read operations (implement `IQuery<T>`)
   - Separate handlers for each operation

4. **Result Pattern**:
   - NO exceptions for business rule violations
   - Use `Result` and `Result<T>` for operation outcomes
   - Return explicit success/failure states with `Error` details

## Coding Conventions

### C# Style

- **Modern C# Features**: Use C# 12 features (primary constructors, records, nullable reference types)
- **Sealed Classes**: Use `sealed` for classes not designed for inheritance
- **Records**: Use records for DTOs, commands, queries, and value objects
- **Primary Constructors**: Use for controllers and services (e.g., `BookingsController(ISender sender)`)
- **Nullable Reference Types**: Enabled - use `?` for nullable types explicitly

### Naming Conventions

- **Classes/Interfaces**: PascalCase (`BookingService`, `IRepository`)
- **Methods/Properties**: PascalCase (`GetBooking`, `TotalPrice`)
- **Parameters/Variables**: camelCase (`apartmentId`, `utcNow`)
- **Private Fields**: camelCase with no prefix (`duration`, NOT `_duration`)
- **Constants**: PascalCase
- **Database**: snake_case (configured in EF Core)

### File Organization

- **One Class Per File**: Each class/interface in its own file
- **File Name = Type Name**: `Booking.cs` contains `Booking` class
- **Namespace = Folder Path**: `ForqStudio.Domain.Bookings` for `src/ForqStudio.Domain/Bookings/`
- **Feature Folders**: Group by feature/aggregate, not by type

Example structure:
```
ForqStudio.Application/
  Bookings/
    ReserveBooking/
      ReserveBookingCommand.cs
      ReserveBookingCommandHandler.cs
      ReserveBookingCommandValidator.cs
    GetBooking/
      GetBookingQuery.cs
      GetBookingQueryHandler.cs
```

## Domain Layer Patterns

### Entities

- Inherit from `Entity` base class
- Private parameterized constructor for creation
- Private parameterless constructor for EF Core
- Private setters on properties
- Static factory methods for creation (e.g., `Booking.Reserve()`)
- Public methods for state transitions that return `Result`

Example:
```csharp
public sealed class Booking : Entity
{
    private Booking(Guid id, ...) : base(id) { }
    private Booking() { } // EF Core

    public Guid ApartmentId { get; private set; }

    public static Booking Reserve(...)
    {
        var booking = new Booking(...);
        booking.RaiseDomainEvent(new BookingReservedDomainEvent(booking.Id));
        return booking;
    }

    public Result Confirm(DateTime utcNow)
    {
        if (Status != BookingStatus.Reserved)
            return Result.Failure(BookingErrors.NotReserved);

        Status = BookingStatus.Confirmed;
        RaiseDomainEvent(new BookingConfirmedDomainEvent(Id));
        return Result.Success();
    }
}
```

### Value Objects

- Implement as `record` types
- No identity, compared by value
- Immutable
- Examples: `Money`, `Currency`, `Address`, `DateRange`

Example:
```csharp
public sealed record Money(decimal Amount, Currency Currency);
```

### Domain Events

- Implement `IDomainEvent` interface
- Immutable records
- Past tense naming (e.g., `BookingReservedDomainEvent`, `BookingConfirmedDomainEvent`)
- Raised via `RaiseDomainEvent()` method on entities

Example:
```csharp
public sealed record BookingReservedDomainEvent(Guid BookingId) : IDomainEvent;
```

### Domain Errors

- Define errors as static readonly in `*Errors` classes
- Use `Error.NotFound()`, `Error.Failure()`, etc.

Example:
```csharp
public static class BookingErrors
{
    public static Error NotFound = new(
        "Booking.NotFound",
        "The booking with the specified identifier was not found");

    public static Error NotReserved = new(
        "Booking.NotReserved",
        "The booking is not in a reserved state");
}
```

### Repository Interfaces

- Define in Domain layer
- Simple, aggregate-focused methods
- Return domain entities
- Examples: `IBookingRepository`, `IApartmentRepository`

## Application Layer Patterns

### Commands

- Immutable records implementing `ICommand<T>`
- Named with imperative verb: `ReserveBookingCommand`, `ConfirmBookingCommand`
- Return type indicates operation result (e.g., `ICommand<Guid>` for created ID)

Example:
```csharp
public sealed record ReserveBookingCommand(
    Guid ApartmentId,
    Guid UserId,
    DateOnly StartDate,
    DateOnly EndDate) : ICommand<Guid>;
```

### Command Handlers

- Implement `ICommandHandler<TCommand, TResponse>`
- Named `*CommandHandler`
- Handle single responsibility
- Use repository pattern
- Return `Result<T>`

Example:
```csharp
internal sealed class ReserveBookingCommandHandler
    : ICommandHandler<ReserveBookingCommand, Guid>
{
    public async Task<Result<Guid>> Handle(
        ReserveBookingCommand command,
        CancellationToken cancellationToken)
    {
        // Implementation
    }
}
```

### Queries

- Immutable records implementing `IQuery<T>`
- Named with noun: `GetBookingQuery`, `SearchApartmentsQuery`
- Return DTOs, not domain entities

Example:
```csharp
public sealed record GetBookingQuery(Guid BookingId) : IQuery<BookingResponse>;
```

### Query Handlers

- Implement `IQueryHandler<TQuery, TResponse>`
- Can use Dapper for performance
- Can implement `ICachedQuery` for caching

Example:
```csharp
internal sealed class GetBookingQueryHandler
    : IQueryHandler<GetBookingQuery, BookingResponse>
{
    public async Task<Result<BookingResponse>> Handle(
        GetBookingQuery query,
        CancellationToken cancellationToken)
    {
        // Implementation
    }
}
```

### Validators

- Use FluentValidation
- One validator per command
- Named `*CommandValidator`
- Registered automatically via assembly scanning

Example:
```csharp
internal sealed class ReserveBookingCommandValidator
    : AbstractValidator<ReserveBookingCommand>
{
    public ReserveBookingCommandValidator()
    {
        RuleFor(x => x.ApartmentId).NotEmpty();
        RuleFor(x => x.UserId).NotEmpty();
    }
}
```

## API Layer Patterns

### Controllers

- Use primary constructors with `ISender` for MediatR
- Inherit from `ControllerBase`
- Annotate with `[ApiController]`, `[Authorize]`, `[ApiVersion]`
- Route: `api/v{version:apiVersion}/{resource}`
- Return `IActionResult`
- Map requests to commands/queries

Example:
```csharp
[ApiController]
[Authorize]
[ApiVersion(ApiVersions.V1)]
[Route("api/v{version:apiVersion}/bookings")]
public class BookingsController(ISender sender) : ControllerBase
{
    [HttpGet("{id}")]
    public async Task<IActionResult> GetBooking(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetBookingQuery(id), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : NotFound();
    }
}
```

### Result Mapping

- `result.IsSuccess` → `Ok(result.Value)` (200)
- `result.IsFailure` with NotFound error → `NotFound()` (404)
- `result.IsFailure` with validation error → `BadRequest(result.Error)` (400)
- `result.IsFailure` (general) → `InternalServerError(result.Error)` (500)
- Successful POST → `CreatedAtAction()` (201)

## Infrastructure Layer Patterns

### Entity Configurations

- Implement `IEntityTypeConfiguration<T>`
- Configure all entity properties explicitly
- Use snake_case for database naming
- Configure value object conversions
- Located in `Configurations/` folder

Example:
```csharp
internal sealed class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.ToTable("bookings");
        builder.HasKey(b => b.Id);
        // ... more configuration
    }
}
```

### Repository Implementations

- Inherit from `Repository<T>`
- Implement domain repository interface
- Use `DbContext` for data access

Example:
```csharp
internal sealed class BookingRepository : Repository<Booking>, IBookingRepository
{
    public BookingRepository(ApplicationDbContext dbContext)
        : base(dbContext)
    {
    }
}
```

## Testing Conventions

### Unit Tests

- Project naming: `*.UnitTests`
- Test class naming: `{ClassUnderTest}Tests`
- Test method naming: `{MethodUnderTest}_{Scenario}_{ExpectedResult}`
- Use NSubstitute for mocking
- Use FluentAssertions for assertions

Example:
```csharp
public class BookingTests
{
    [Fact]
    public void Confirm_WhenNotReserved_ShouldReturnFailure()
    {
        // Arrange
        var booking = /* ... */;

        // Act
        var result = booking.Confirm(DateTime.UtcNow);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(BookingErrors.NotReserved);
    }
}
```

### Integration Tests

- Project naming: `*.IntegrationTests`
- Test against real database/infrastructure
- Use `WebApplicationFactory` for API tests
- Clean up test data after each test

### Functional Tests

- Project naming: `*.FunctionalTests`
- End-to-end API testing
- Test complete workflows

## Common Development Tasks

### Adding a New Feature (Command)

1. **Domain**: Create/modify aggregate with business logic
2. **Domain**: Add domain event if needed
3. **Domain**: Add domain errors for validation
4. **Application**: Create command record implementing `ICommand<T>`
5. **Application**: Create command handler implementing `ICommandHandler<,>`
6. **Application**: Create validator inheriting `AbstractValidator<>`
7. **API**: Add controller endpoint mapping request to command
8. **Infrastructure**: Add repository method if needed
9. **Tests**: Write unit tests for domain logic
10. **Tests**: Write integration tests for handler
11. **Tests**: Write functional tests for API endpoint

### Adding a New Query

1. **Application**: Create query record implementing `IQuery<T>`
2. **Application**: Create response DTO
3. **Application**: Create query handler implementing `IQueryHandler<,>`
4. **Application**: Optionally implement `ICachedQuery` for caching
5. **API**: Add controller endpoint
6. **Infrastructure**: Add Dapper query if needed for performance
7. **Tests**: Write integration/functional tests

### Adding a New Aggregate

1. **Domain**: Create entity inheriting from `Entity`
2. **Domain**: Create value objects as records
3. **Domain**: Create domain events
4. **Domain**: Create repository interface
5. **Domain**: Create domain errors
6. **Infrastructure**: Create entity configuration
7. **Infrastructure**: Implement repository
8. **Infrastructure**: Add DbSet to ApplicationDbContext
9. **Infrastructure**: Create and run migration
10. **Tests**: Write unit tests for domain logic

## Important Guidelines

### Do's

- Always use the Result pattern for operations that can fail
- Raise domain events for significant state changes
- Keep domain layer free of infrastructure concerns
- Use value objects for primitive obsession
- Validate using FluentValidation in application layer
- Use async/await for all I/O operations
- Include CancellationToken in all async methods
- Use IDateTimeProvider instead of DateTime.UtcNow for testability
- Use private setters on entity properties
- Group by feature/aggregate, not by technical concern

### Don'ts

- Don't throw exceptions for business rule violations (use Result pattern)
- Don't reference Infrastructure or API from Domain
- Don't reference API from Application
- Don't put business logic in Application layer (belongs in Domain)
- Don't return domain entities from API (use DTOs)
- Don't use public setters on entities
- Don't create anemic domain models
- Don't skip validation on commands
- Don't use DateTime.UtcNow directly (use IDateTimeProvider)

## Key Dependencies

- **MediatR**: CQRS and mediator pattern
- **FluentValidation**: Input validation
- **Entity Framework Core**: Data access with PostgreSQL
- **Dapper**: High-performance queries
- **StackExchange.Redis**: Distributed caching
- **Quartz.NET**: Background job scheduling
- **Serilog**: Structured logging
- **xUnit**: Test framework
- **NSubstitute**: Mocking framework
- **FluentAssertions**: Fluent assertion library

## Database Migrations

```bash
# Add migration
dotnet ef migrations add MigrationName --project src/ForqStudio.Infrastructure --startup-project src/ForqStudio.Api

# Update database
dotnet ef database update --project src/ForqStudio.Infrastructure --startup-project src/ForqStudio.Api
```

## Running the Application

```bash
# Start infrastructure (PostgreSQL, Redis, Keycloak)
docker-compose up -d

# Run API
dotnet run --project src/ForqStudio.Api

# Run tests
dotnet test
```

## Authentication & Authorization

- **Identity Provider**: Keycloak
- **Authentication**: JWT Bearer tokens
- **Authorization**: Permission-based with custom `[HasPermission]` attribute
- **User Context**: Accessed via `IUserContext` interface

## Outbox Pattern

Domain events are stored as outbox messages in the database for eventual consistency:

1. Domain events raised during command execution
2. Events saved as outbox messages in same transaction
3. Background job (Quartz.NET) processes outbox messages
4. Guarantees at-least-once delivery

## Error Handling

- Business rule violations: Return `Result.Failure(error)`
- Validation errors: Caught by ValidationBehavior pipeline
- Exceptions: Caught by middleware, converted to ProblemDetails
- Domain errors: Static readonly Error instances in `*Errors` classes

## Performance Considerations

- Use Dapper for read-heavy queries
- Implement `ICachedQuery` for cacheable queries
- Redis for distributed caching
- AsNoTracking() for read-only EF queries
- Pagination for list endpoints
