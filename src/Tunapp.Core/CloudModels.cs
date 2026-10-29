namespace Tunapp.Core;

public sealed record CloudBusiness(Guid Id, string Name, Role Role);
public sealed record CloudMember(string UserId, string Email, Role Role);
public sealed record BusinessInput(Guid Id, string Name);
public sealed record MemberInput(string Email, Role Role);
public sealed record CampaignInput(Guid Id, string Name, DateOnly? Start, DateOnly? End);
public sealed record PlotInput(Guid Id, string Name, decimal? Hectares, int OliveTrees, string Variety, string Notes);
public sealed record CampaignPlotInput(decimal? Hectares, int OliveTrees, string Variety, string Notes);
public sealed record CloseCampaignInput(bool Closed);
public sealed record ApiError(string Message);
public sealed record CloudSnapshot(CloudBusiness Business, List<Campaign> Campaigns, List<Plot> Plots,
    List<CampaignPlot> CampaignPlots, List<CloudMember> Members);
