namespace NepalParcel.Domain.Branches;

/// <summary>
/// The role a customer-facing location plays. Physical storage / sorting hubs are modelled by
/// <see cref="Warehouses.Warehouse"/>, which belongs to a branch.
/// </summary>
public enum BranchType
{
    HeadOffice = 1,
    Branch = 2,
}
