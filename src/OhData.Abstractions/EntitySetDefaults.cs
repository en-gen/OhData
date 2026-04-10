namespace OhData.Abstractions;

public class EntitySetDefaults
{
    public bool SelectEnabled { get; set; }
    public bool ExpandEnabled { get; set; }
    public bool FilterEnabled { get; set; }
    public bool OrderByEnabled { get; set; }
    public bool CountEnabled { get; set; }
    private int? _maxTop = 1000;
    public int? MaxTop
    {
        get => _maxTop;
        set
        {
            if (value is <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxTop), value, "MaxTop must be a positive integer or null.");
            _maxTop = value;
        }
    }
}