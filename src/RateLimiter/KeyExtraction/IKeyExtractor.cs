namespace RateLimiter.KeyExtraction;

public interface IKeyExtractor
{
    string Extract(HttpContext context);
}
