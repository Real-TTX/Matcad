using Matcad.Config;

namespace Matcad.Controls;

/// <summary>What the host cell of a route row needs (<c>_RouteName.cshtml</c>): the route, its label under the base domain and, when the page wants the warning, its certificate.</summary>
public sealed record RouteNameModel(RouteConfig Route, string Label, string BaseDomain, CertificatePlanner.Coverage? Cert = null);
