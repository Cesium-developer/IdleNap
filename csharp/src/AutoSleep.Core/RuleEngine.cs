using System;
using System.Collections;
using System.Collections.Generic;

namespace AutoSleep.Core
{
    public class RuleResult
    {
        public bool Idle { get; set; }
        public string Action { get; set; }
    }

    public class RuleEngine
    {
        public RuleResult Evaluate(object tree, Dictionary<string, bool> values)
        {
            if (tree == null)
                return new RuleResult { Idle = false, Action = "none" };

            return EvaluateNode(tree as Dictionary<string, object>, values);
        }

        private RuleResult EvaluateNode(Dictionary<string, object> node, Dictionary<string, bool> values)
        {
            if (node == null)
                return new RuleResult { Idle = false, Action = "none" };

            string type = node.ContainsKey("type") ? node["type"] as string : "";

            switch (type)
            {
                case "condition":
                    return EvaluateCondition(node, values);
                case "operator":
                case "logic":
                    return EvaluateOperator(node, values);
                case "control":
                    return EvaluateControl(node, values);
                case "action":
                    return EvaluateAction(node);
                case "sequence":
                    return EvaluateSequence(node, values);
                case "program":
                    return EvaluateProgram(node, values);
                default:
                    return new RuleResult { Idle = false, Action = "none" };
            }
        }

        // JavaScriptSerializer 反序列化 JSON 数组为 ArrayList 而非 List<object>
        // 此辅助函数兼容两种类型
        private List<object> ToList(object obj)
        {
            if (obj == null) return null;
            if (obj is List<object>) return (List<object>)obj;
            if (obj is ArrayList)
            {
                ArrayList al = (ArrayList)obj;
                List<object> result = new List<object>(al.Count);
                for (int i = 0; i < al.Count; i++)
                    result.Add(al[i]);
                return result;
            }
            return null;
        }

        private RuleResult EvaluateSequence(Dictionary<string, object> node, Dictionary<string, bool> values)
        {
            List<object> actions = ToList(node.ContainsKey("actions") ? node["actions"] : null);
            if (actions == null || actions.Count == 0)
                return new RuleResult { Idle = false, Action = "none" };

            RuleResult result = new RuleResult { Idle = false, Action = "none" };
            foreach (var action in actions)
            {
                result = EvaluateNode(action as Dictionary<string, object>, values);
            }
            return result;
        }

        private RuleResult EvaluateProgram(Dictionary<string, object> node, Dictionary<string, bool> values)
        {
            List<object> actions = ToList(node.ContainsKey("actions") ? node["actions"] : null);
            if (actions == null || actions.Count == 0)
                return new RuleResult { Idle = false, Action = "none" };

            RuleResult result = new RuleResult { Idle = false, Action = "none" };
            foreach (var action in actions)
            {
                result = EvaluateNode(action as Dictionary<string, object>, values);
            }
            return result;
        }

        private RuleResult EvaluateCondition(Dictionary<string, object> node, Dictionary<string, bool> values)
        {
            string cond = node.ContainsKey("condition") ? node["condition"] as string : "";
            if (string.IsNullOrEmpty(cond))
                return new RuleResult { Idle = false, Action = "none" };

            if (values.ContainsKey(cond))
                return new RuleResult { Idle = values[cond], Action = "none" };

            return new RuleResult { Idle = false, Action = "none" };
        }

        private RuleResult EvaluateOperator(Dictionary<string, object> node, Dictionary<string, bool> values)
        {
            string op = "";
            if (node.ContainsKey("operator")) op = node["operator"] as string;
            else if (node.ContainsKey("op")) op = node["op"] as string;
            List<object> children = ToList(node.ContainsKey("children") ? node["children"] : null);

            if (children == null || children.Count == 0)
                return new RuleResult { Idle = true, Action = "none" };

            if (op == "AND")
            {
                foreach (var child in children)
                {
                    var result = EvaluateNode(child as Dictionary<string, object>, values);
                    if (!result.Idle)
                        return new RuleResult { Idle = false, Action = "none" };
                }
                return new RuleResult { Idle = true, Action = "none" };
            }
            else if (op == "OR")
            {
                foreach (var child in children)
                {
                    var result = EvaluateNode(child as Dictionary<string, object>, values);
                    if (result.Idle)
                        return new RuleResult { Idle = true, Action = "none" };
                }
                return new RuleResult { Idle = false, Action = "none" };
            }
            else if (op == "NOT")
            {
                var result = EvaluateNode(children[0] as Dictionary<string, object>, values);
                return new RuleResult { Idle = !result.Idle, Action = "none" };
            }

            return new RuleResult { Idle = false, Action = "none" };
        }

        private RuleResult EvaluateControl(Dictionary<string, object> node, Dictionary<string, bool> values)
        {
            // if 分支
            if (node.ContainsKey("condition") && node["condition"] != null)
            {
                var condResult = EvaluateNode(node["condition"] as Dictionary<string, object>, values);
                if (condResult.Idle && node.ContainsKey("then") && node["then"] != null)
                {
                    return EvaluateNode(node["then"] as Dictionary<string, object>, values);
                }
            }

            // elif 分支
            if (node.ContainsKey("elif"))
            {
                List<object> elifList = ToList(node["elif"]);
                if (elifList != null)
                {
                    foreach (var elifItem in elifList)
                    {
                        var elif = elifItem as Dictionary<string, object>;
                        if (elif != null && elif.ContainsKey("condition") && elif["condition"] != null)
                        {
                            var condResult = EvaluateNode(elif["condition"] as Dictionary<string, object>, values);
                            if (condResult.Idle && elif.ContainsKey("then") && elif["then"] != null)
                            {
                                return EvaluateNode(elif["then"] as Dictionary<string, object>, values);
                            }
                        }
                    }
                }
            }

            // else 分支
            if (node.ContainsKey("else") && node["else"] != null)
            {
                return EvaluateNode(node["else"] as Dictionary<string, object>, values);
            }

            return new RuleResult { Idle = false, Action = "none" };
        }

        private RuleResult EvaluateAction(Dictionary<string, object> node)
        {
            string action = node.ContainsKey("action") ? node["action"] as string : "";

            switch (action)
            {
                case "reset_timer":
                    return new RuleResult { Idle = false, Action = "reset_timer" };
                case "continue_timer":
                    return new RuleResult { Idle = true, Action = "continue_timer" };
                case "sleep":
                    return new RuleResult { Idle = true, Action = "sleep" };
                case "nothing":
                    return new RuleResult { Idle = false, Action = "nothing" };
                default:
                    return new RuleResult { Idle = true, Action = "none" };
            }
        }
    }
}
