using System.Text.Json;
using FluentAssertions;

namespace ForqStudio.Application.UnitTests.Sandboxes;

public class NatsSandboxEventPublisherPayloadTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
    };

    [Fact]
    public void SpawnPayload_SerializesAsSnakeCase_WithExpectedFields()
    {
        var sandboxId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var ownerUserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var payload = new SpawnPayload(sandboxId.ToString(), 4, 4096, ownerUserId.ToString());

        var json = JsonSerializer.Serialize(payload, Options);

        var doc = JsonDocument.Parse(json).RootElement;

        doc.GetProperty("id").GetString().Should().Be(sandboxId.ToString());
        doc.GetProperty("vcpus").GetInt32().Should().Be(4);
        doc.GetProperty("memory_mib").GetInt32().Should().Be(4096);
        doc.GetProperty("customer_id").GetString().Should().Be(ownerUserId.ToString());
    }

    [Fact]
    public void DestroyPayload_SerializesIdOnly()
    {
        var sandboxId = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new DestroyPayload(sandboxId.ToString()), Options);

        var doc = JsonDocument.Parse(json).RootElement;
        doc.GetProperty("id").GetString().Should().Be(sandboxId.ToString());
    }

    private sealed record SpawnPayload(string Id, int Vcpus, int MemoryMib, string CustomerId);

    private sealed record DestroyPayload(string Id);
}
