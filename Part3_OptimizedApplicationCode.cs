using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace WarehouseX.OrderManagement.Services
{
    /// ========================================================================
    /// WarehouseX Order Management System: Optimized Service Layer
    /// Performance Optimization & Scalability Course - Part 3
    /// ========================================================================
    
    /// <summary>
    /// Production-ready optimized service for retrieving and processing orders.
    /// 
    /// KEY OPTIMIZATIONS:
    /// 1. Async/await pattern for non-blocking I/O (ToListAsync, FirstOrDefaultAsync)
    /// 2. Task.WhenAll for parallel independent queries
    /// 3. Distributed Redis cache with Cache-Aside pattern
    /// 4. Proper DbContext scoping (injected, not shared)
    /// 5. Pagination support for memory efficiency
    /// 6. Column projection to reduce data transfer
    /// </summary>
    public interface IOrderService
    {
        // Async method for fetching customer orders with details
        Task<PagedResult<CustomerOrderDTO>> GetCustomerOrdersAsync(
            int customerId, 
            int pageNumber = 1, 
            int pageSize = 50, 
            CancellationToken cancellationToken = default);

        // Async method for fetching single order with all related data
        Task<OrderDetailDTO> GetOrderWithDetailsAsync(
            int orderId, 
            CancellationToken cancellationToken = default);

        // Async batch operation for processing multiple orders
        Task<BatchProcessResult> ProcessOrdersAsync(
            IEnumerable<int> orderIds, 
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Dependency-injected service implementing IOrderService.
    /// 
    /// LIFETIME: Scoped
    /// - DbContext: Scoped (fresh instance per HTTP request, proper async cleanup)
    /// - IDistributedCache: Singleton (thread-safe Redis client)
    /// - ILogger: Scoped (correlates logs to HTTP request)
    /// </summary>
    public class OptimizedOrderService : IOrderService
    {
        private readonly WarehouseXDbContext _dbContext;
        private readonly IDistributedCache _distributedCache;
        private readonly ILogger<OptimizedOrderService> _logger;

        // Cache configuration constants
        private const string CACHE_KEY_PREFIX = "order:";
        private const int ABSOLUTE_EXPIRATION_MINUTES = 15;
        private const int SLIDING_EXPIRATION_MINUTES = 5;

        public OptimizedOrderService(
            WarehouseXDbContext dbContext,
            IDistributedCache distributedCache,
            ILogger<OptimizedOrderService> logger)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _distributedCache = distributedCache ?? throw new ArgumentNullException(nameof(distributedCache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// ====================================================================
        /// QUERY 1: Get Customer Orders with Pagination
        /// 
        /// Demonstrates:
        /// - Async/await for non-blocking database I/O
        /// - Explicit JOINs with column projection (no SELECT *)
        /// - Cache-Aside pattern for customer order lists
        /// - Pagination using Skip/Take for memory efficiency
        /// ====================================================================
        public async Task<PagedResult<CustomerOrderDTO>> GetCustomerOrdersAsync(
            int customerId,
            int pageNumber = 1,
            int pageSize = 50,
            CancellationToken cancellationToken = default)
        {
            // Input validation
            if (customerId <= 0)
                throw new ArgumentException("Customer ID must be positive", nameof(customerId));

            if (pageNumber < 1) pageNumber = 1;
            if (pageSize < 1 || pageSize > 1000) pageSize = 50;

            _logger.LogInformation(
                "Retrieving orders for customer {CustomerId}, page {PageNumber}, pageSize {PageSize}",
                customerId, pageNumber, pageSize);

            try
            {
                // CACHE-ASIDE PATTERN: Check cache first
                var cacheKey = $"{CACHE_KEY_PREFIX}customer:{customerId}:page:{pageNumber}:size:{pageSize}";
                var cachedResult = await _distributedCache.GetStringAsync(cacheKey, cancellationToken);

                if (!string.IsNullOrEmpty(cachedResult))
                {
                    _logger.LogDebug("Cache hit for customer orders: {CacheKey}", cacheKey);
                    return JsonConvert.DeserializeObject<PagedResult<CustomerOrderDTO>>(cachedResult);
                }

                _logger.LogDebug("Cache miss for customer orders: {CacheKey}", cacheKey);

                // CACHE MISS: Query database
                // Calculate pagination offset
                var skipCount = (pageNumber - 1) * pageSize;

                // ASYNC QUERY 1: Fetch paginated orders with customer details
                // Uses explicit INNER JOIN with column projection (no SELECT *)
                var ordersQuery = _dbContext.Orders
                    .AsNoTracking()  // Read-only queries don't need change tracking
                    .Where(o => o.CustomerId == customerId && o.OrderDate >= DateTime.UtcNow.AddMonths(-12))
                    .OrderByDescending(o => o.OrderDate)
                    .Skip(skipCount)
                    .Take(pageSize)
                    .Select(o => new CustomerOrderDTO
                    {
                        OrderId = o.OrderId,
                        OrderNumber = o.OrderNumber,
                        OrderDate = o.OrderDate,
                        OrderStatus = o.OrderStatus,
                        TotalAmount = o.TotalAmount,
                        CustomerName = o.Customer.CustomerName,
                        CustomerEmail = o.Customer.Email,
                        ItemCount = o.OrderDetails.Count,
                        HoursAgo = (int)DateTime.UtcNow.Subtract(o.OrderDate).TotalHours
                    });

                // ASYNC QUERY 2: Get total count for pagination info (using COUNT before SKIP)
                // Separate query to avoid inefficient pagination with COUNT
                var countQuery = _dbContext.Orders
                    .AsNoTracking()
                    .Where(o => o.CustomerId == customerId && o.OrderDate >= DateTime.UtcNow.AddMonths(-12))
                    .CountAsync(cancellationToken);

                // PARALLEL EXECUTION: Execute both queries concurrently
                // Task.WhenAll enables waiting for multiple independent I/O operations
                var ordersTask = ordersQuery.ToListAsync(cancellationToken);
                await Task.WhenAll(ordersTask, countQuery);

                var orders = await ordersTask;
                var totalCount = await countQuery;

                // Build result object
                var result = new PagedResult<CustomerOrderDTO>
                {
                    Items = orders,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    TotalCount = totalCount,
                    TotalPages = (totalCount + pageSize - 1) / pageSize
                };

                // CACHE UPDATE: Store in Redis with expiration
                var cacheOptions = new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(ABSOLUTE_EXPIRATION_MINUTES),
                    SlidingExpiration = TimeSpan.FromMinutes(SLIDING_EXPIRATION_MINUTES)
                };

                var serializedResult = JsonConvert.SerializeObject(result);
                await _distributedCache.SetStringAsync(cacheKey, serializedResult, cacheOptions, cancellationToken);

                _logger.LogInformation(
                    "Successfully retrieved {OrderCount} orders for customer {CustomerId}",
                    orders.Count, customerId);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Order retrieval cancelled for customer {CustomerId}", customerId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error retrieving orders for customer {CustomerId}: {ErrorMessage}",
                    customerId, ex.Message);
                throw;
            }
        }

        /// ====================================================================
        /// QUERY 2: Get Single Order with All Details
        /// 
        /// Demonstrates:
        /// - Parallel loading of related entities (OrderDetails, Products)
        /// - Per-entity caching with invalidation strategy
        /// - Efficient eager loading with explicit column selection
        /// ====================================================================
        public async Task<OrderDetailDTO> GetOrderWithDetailsAsync(
            int orderId,
            CancellationToken cancellationToken = default)
        {
            if (orderId <= 0)
                throw new ArgumentException("Order ID must be positive", nameof(orderId));

            _logger.LogInformation("Retrieving order details for order {OrderId}", orderId);

            try
            {
                // CACHE-ASIDE PATTERN: Check cache for full order details
                var cacheKey = $"{CACHE_KEY_PREFIX}detail:{orderId}";
                var cachedOrder = await _distributedCache.GetStringAsync(cacheKey, cancellationToken);

                if (!string.IsNullOrEmpty(cachedOrder))
                {
                    _logger.LogDebug("Cache hit for order details: {CacheKey}", cacheKey);
                    return JsonConvert.DeserializeObject<OrderDetailDTO>(cachedOrder);
                }

                // CACHE MISS: Query database
                // PARALLEL QUERIES: Fetch order header and details concurrently
                
                // Query 1: Fetch order with customer info (ASYNC)
                var orderTask = _dbContext.Orders
                    .AsNoTracking()
                    .Where(o => o.OrderId == orderId)
                    .Select(o => new
                    {
                        o.OrderId,
                        o.OrderNumber,
                        o.OrderDate,
                        o.OrderStatus,
                        o.TotalAmount,
                        CustomerName = o.Customer.CustomerName,
                        CustomerEmail = o.Customer.Email,
                        CustomerId = o.CustomerId
                    })
                    .FirstOrDefaultAsync(cancellationToken);

                // Query 2: Fetch order details with product info (ASYNC)
                var detailsTask = _dbContext.OrderDetails
                    .AsNoTracking()
                    .Where(od => od.OrderId == orderId)
                    .Select(od => new OrderItemDTO
                    {
                        OrderDetailId = od.OrderDetailId,
                        ProductId = od.ProductId,
                        ProductName = od.Product.ProductName,
                        SKU = od.Product.SKU,
                        Quantity = od.Quantity,
                        UnitPrice = od.UnitPrice,
                        LineTotal = od.Quantity * od.UnitPrice
                    })
                    .ToListAsync(cancellationToken);

                // PARALLEL EXECUTION: Both queries run concurrently (non-blocking I/O)
                var orderHeader = await orderTask;
                var orderDetails = await detailsTask;

                // NULL CHECK: Order not found
                if (orderHeader == null)
                {
                    _logger.LogWarning("Order not found: {OrderId}", orderId);
                    throw new KeyNotFoundException($"Order {orderId} not found");
                }

                // Build composite result object
                var result = new OrderDetailDTO
                {
                    OrderId = orderHeader.OrderId,
                    OrderNumber = orderHeader.OrderNumber,
                    OrderDate = orderHeader.OrderDate,
                    OrderStatus = orderHeader.OrderStatus,
                    TotalAmount = orderHeader.TotalAmount,
                    CustomerName = orderHeader.CustomerName,
                    CustomerEmail = orderHeader.CustomerEmail,
                    CustomerId = orderHeader.CustomerId,
                    Items = orderDetails,
                    ItemCount = orderDetails.Count,
                    TotalQuantity = orderDetails.Sum(x => x.Quantity)
                };

                // CACHE UPDATE: Store with aggressive expiration (5 minutes)
                var cacheOptions = new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
                    SlidingExpiration = TimeSpan.FromMinutes(2)
                };

                var serializedOrder = JsonConvert.SerializeObject(result);
                await _distributedCache.SetStringAsync(cacheKey, serializedOrder, cacheOptions, cancellationToken);

                _logger.LogInformation(
                    "Successfully retrieved order {OrderId} with {ItemCount} items",
                    orderId, orderDetails.Count);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Order detail retrieval cancelled for order {OrderId}", orderId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error retrieving order details for order {OrderId}: {ErrorMessage}",
                    orderId, ex.Message);
                throw;
            }
        }

        /// ====================================================================
        /// BATCH OPERATION: Process Multiple Orders
        /// 
        /// Demonstrates:
        /// - Batch processing with parallel execution limits
        /// - Semaphore-based throttling to prevent resource exhaustion
        /// - Transaction management for consistency
        /// ====================================================================
        public async Task<BatchProcessResult> ProcessOrdersAsync(
            IEnumerable<int> orderIds,
            CancellationToken cancellationToken = default)
        {
            if (orderIds == null || !orderIds.Any())
                throw new ArgumentException("Order IDs collection cannot be null or empty", nameof(orderIds));

            var orderIdList = orderIds.ToList();
            _logger.LogInformation("Processing batch of {OrderCount} orders", orderIdList.Count);

            var result = new BatchProcessResult
            {
                TotalCount = orderIdList.Count,
                SuccessCount = 0,
                FailureCount = 0,
                ProcessingErrors = new List<string>()
            };

            try
            {
                // THROTTLING: Limit parallel executions to 5 concurrent orders
                // Prevents thread pool starvation and database connection exhaustion
                using (var semaphore = new SemaphoreSlim(5, 5))
                {
                    var processingTasks = orderIdList.Select(async orderId =>
                    {
                        await semaphore.WaitAsync(cancellationToken);
                        try
                        {
                            // Process individual order
                            var order = await _dbContext.Orders
                                .FirstOrDefaultAsync(o => o.OrderId == orderId, cancellationToken);

                            if (order != null)
                            {
                                // Simulate business logic (e.g., validate, update status)
                                order.OrderStatus = "Processing";
                                order.LastModifiedDate = DateTime.UtcNow;

                                await _dbContext.SaveChangesAsync(cancellationToken);

                                // Invalidate cache for this order
                                await _distributedCache.RemoveAsync(
                                    $"{CACHE_KEY_PREFIX}detail:{orderId}", cancellationToken);

                                Interlocked.Increment(ref result.SuccessCount);
                                _logger.LogDebug("Successfully processed order {OrderId}", orderId);
                            }
                            else
                            {
                                throw new KeyNotFoundException($"Order {orderId} not found");
                            }
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref result.FailureCount);
                            result.ProcessingErrors.Add($"Order {orderId}: {ex.Message}");
                            _logger.LogError(ex, "Failed to process order {OrderId}", orderId);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });

                    // PARALLEL EXECUTION: All tasks run concurrently with throttle
                    await Task.WhenAll(processingTasks);
                }

                _logger.LogInformation(
                    "Batch processing completed: {SuccessCount} succeeded, {FailureCount} failed",
                    result.SuccessCount, result.FailureCount);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch processing failed with error: {ErrorMessage}", ex.Message);
                throw;
            }
        }
    }

    // ========================================================================
    // DTOs (Data Transfer Objects) - Optimized for API responses
    // ========================================================================

    public class CustomerOrderDTO
    {
        public int OrderId { get; set; }
        public string OrderNumber { get; set; }
        public DateTime OrderDate { get; set; }
        public string OrderStatus { get; set; }
        public decimal TotalAmount { get; set; }
        public string CustomerName { get; set; }
        public string CustomerEmail { get; set; }
        public int ItemCount { get; set; }
        public int HoursAgo { get; set; }
    }

    public class OrderDetailDTO
    {
        public int OrderId { get; set; }
        public string OrderNumber { get; set; }
        public DateTime OrderDate { get; set; }
        public string OrderStatus { get; set; }
        public decimal TotalAmount { get; set; }
        public string CustomerName { get; set; }
        public string CustomerEmail { get; set; }
        public int CustomerId { get; set; }
        public List<OrderItemDTO> Items { get; set; }
        public int ItemCount { get; set; }
        public int TotalQuantity { get; set; }
    }

    public class OrderItemDTO
    {
        public int OrderDetailId { get; set; }
        public int ProductId { get; set; }
        public string ProductName { get; set; }
        public string SKU { get; set; }
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal LineTotal { get; set; }
    }

    public class PagedResult<T>
    {
        public List<T> Items { get; set; } = new();
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
    }

    public class BatchProcessResult
    {
        public int TotalCount { get; set; }
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public List<string> ProcessingErrors { get; set; }
    }

    // ========================================================================
    // Database Context Configuration Example
    // ========================================================================

    public abstract class WarehouseXDbContext : DbContext
    {
        public DbSet<Order> Orders { get; set; }
        public DbSet<OrderDetail> OrderDetails { get; set; }
        public DbSet<Customer> Customers { get; set; }
        public DbSet<Product> Products { get; set; }
    }

    // Entity definitions (simplified)
    public class Order
    {
        public int OrderId { get; set; }
        public string OrderNumber { get; set; }
        public int CustomerId { get; set; }
        public Customer Customer { get; set; }
        public DateTime OrderDate { get; set; }
        public string OrderStatus { get; set; }
        public decimal TotalAmount { get; set; }
        public DateTime LastModifiedDate { get; set; }
        public List<OrderDetail> OrderDetails { get; set; }
    }

    public class OrderDetail
    {
        public int OrderDetailId { get; set; }
        public int OrderId { get; set; }
        public int ProductId { get; set; }
        public Order Order { get; set; }
        public Product Product { get; set; }
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
    }

    public class Customer
    {
        public int CustomerId { get; set; }
        public string CustomerName { get; set; }
        public string Email { get; set; }
        public string City { get; set; }
        public string Country { get; set; }
        public List<Order> Orders { get; set; }
    }

    public class Product
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; }
        public string SKU { get; set; }
        public decimal UnitPrice { get; set; }
        public int StockLevel { get; set; }
    }

    // ========================================================================
    // Dependency Injection Registration Example (Startup.cs)
    // ========================================================================

    /*
    public void ConfigureServices(IServiceCollection services)
    {
        // DbContext: Scoped lifetime (new instance per HTTP request)
        services.AddDbContext<WarehouseXDbContext>(options =>
            options.UseSqlServer(Configuration.GetConnectionString("DefaultConnection"))
                   .EnableSensitiveDataLogging(false)
                   .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

        // Distributed Cache: Singleton (Redis connection pooled)
        services.AddStackExchangeRedisCache(options =>
            options.Configuration = Configuration.GetConnectionString("Redis"));

        // Service: Scoped lifetime
        services.AddScoped<IOrderService, OptimizedOrderService>();

        // Logging
        services.AddLogging(config =>
            config.AddConsole()
                  .AddDebug()
                  .AddApplicationInsights());

        services.AddControllers();
    }
    */
}
