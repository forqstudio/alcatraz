namespace ForqStudio.Infrastructure.Messaging;

public sealed class NatsOptions
{
    public string Url { get; set; } = "nats://localhost:4222";

    public string SpawnSubject { get; set; } = "vm.spawn";

    public string DestroySubject { get; set; } = "vm.destroy";
}
