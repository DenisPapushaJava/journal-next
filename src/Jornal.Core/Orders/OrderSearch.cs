namespace Jornal.Core.Orders;

public static class OrderSearch
{
    public static IEnumerable<OrderSummary> Filter(IEnumerable<OrderSummary> orders, string? searchText)
    {
        ArgumentNullException.ThrowIfNull(orders);
        var query = new OrderSearchQuery(searchText);
        if (query.Text.Length == 0)
        {
            return orders;
        }

        return orders.Where(query.Matches);
    }

    public static bool Matches(OrderSummary order, string query) => new OrderSearchQuery(query).Matches(order);
}
