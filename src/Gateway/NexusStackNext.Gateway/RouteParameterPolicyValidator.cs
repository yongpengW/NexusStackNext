using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.AspNetCore.Routing.Patterns;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Configuration.RouteValidators;

namespace NexusStackNext.Gateway;

/// <summary>在启动与热更新时解析参数约束，防止错误延迟到请求匹配器构建时才出现。</summary>
/// <param name="policies">宿主实际使用的参数策略工厂。</param>
public sealed class RouteParameterPolicyValidator(ParameterPolicyFactory policies) : IRouteValidator
{
    /// <inheritdoc />
    public ValueTask ValidateAsync(RouteConfig routeConfig, IList<Exception> errors)
    {
        ArgumentNullException.ThrowIfNull(routeConfig);
        ArgumentNullException.ThrowIfNull(errors);
        if (string.IsNullOrEmpty(routeConfig.Match.Path))
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            var pattern = RoutePatternFactory.Parse(routeConfig.Match.Path);
            foreach (var parameter in pattern.Parameters)
            {
                foreach (var policy in parameter.ParameterPolicies)
                {
                    var resolved = policies.Create(parameter, policy);
                    if (resolved is OptionalRouteConstraint optional)
                    {
                        resolved = optional.InnerConstraint;
                    }

                    if (resolved is RegexRouteConstraint regex)
                    {
                        // 框架延迟编译正则；创建策略成功不代表表达式合法。
                        _ = regex.Constraint;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or RoutePatternException or RouteCreationException)
        {
            errors.Add(exception);
        }

        return ValueTask.CompletedTask;
    }
}
