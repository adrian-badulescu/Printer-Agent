using StackExchange.Redis;

namespace PrinterAgent.Infrastructure.Redis;

public interface IRedisConnectionMultiplexerHolder
{
    IConnectionMultiplexer Get();

    void Reset();
}
