using System;
using System.Collections;
using System.Collections.Generic;

namespace AutoSleep.Core
{
    public class RuleResult
    {
        public bool Idle { get; set; }
        public string Action { get; set; }
        // 评估过程中"空闲语义为 false"的叶子条件名（深度优先、去重）——仅供日志原因行使用，不参与判定
        public List<string> FailedConditions { get; set; }
    }

    public class RuleEngine
    {
        public RuleResult Evaluate(object tree, Dictionary<string, bool> values, Dictionary<string, double> metrics = null)
        {
            if (tree == null)
                return new RuleResult { Idle = false, Action = "none" };

            var failed = new List<string>();
            var result = EvaluateNode(tree as Dictionary<string, object>, values, metrics, failed);
            result.FailedConditions = failed;
            return result;
        }

        private RuleResult EvaluateNode(Dictionary<string, object> node, Dictionary<string, bool> values, Dictionary<string, double> metrics, List<string> failed)
        {
            if (node == null)
                return new RuleResult { Idle = false, Action = "none" };

            string type = node.ContainsKey("type") ? node["type"] as string : "";

            switch (type)
            {
                case "condition":
                    return EvaluateCondition(node, values, metrics, failed);
                case "operator":
                case "logic":
                    return EvaluateOperator(node, values, metrics, failed);
                case "control":
                    return EvaluateControl(node, values, metrics, failed);
                case "action":
                    return EvaluateAction(node);
                case "sequence":
                    return EvaluateSequence(node, values, metrics, failed);
                case "program":
                    return EvaluateProgram(node, values, metrics, failed);
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

        private RuleResult EvaluateSequence(Dictionary<string, object> node, Dictionary<string, bool> values, Dictionary<string, double> metrics, List<string> failed)
        {
            List<object> actions = ToList(node.ContainsKey("actions") ? node["actions"] : null);
            if (actions == null || actions.Count == 0)
                return new RuleResult { Idle = false, Action = "none" };

            RuleResult result = new RuleResult { Idle = false, Action = "none" };
            foreach (var action in actions)
            {
                result = EvaluateNode(action as Dictionary<string, object>, values, metrics, failed);
            }
            return result;
        }

        private RuleResult EvaluateProgram(Dictionary<string, object> node, Dictionary<string, bool> values, Dictionary<string, double> metrics, List<string> failed)
        {
            List<object> actions = ToList(node.ContainsKey("actions") ? node["actions"] : null);
            if (actions == null || actions.Count == 0)
                return new RuleResult { Idle = false, Action = "none" };

            RuleResult result = new RuleResult { Idle = false, Action = "none" };
            foreach (var action in actions)
            {
                result = EvaluateNode(action as Dictionary<string, object>, values, metrics, failed);
            }
            return result;
        }

        private RuleResult EvaluateCondition(Dictionary<string, object> node, Dictionary<string, bool> values, Dictionary<string, double> metrics, List<string> failed)
        {
            string cond = node.ContainsKey("condition") ? node["condition"] as string : "";
            if (string.IsNullOrEmpty(cond))
                return new RuleResult { Idle = false, Action = "none" };

            bool idle;
            // 节点自带数值（编辑器保存阶段已补齐）：原始指标 vs 节点阈值
            if (node.ContainsKey("value") && metrics != null && metrics.ContainsKey(cond))
            {
                double metric = metrics[cond];
                double threshold;
                if (!TryGetDouble(node["value"], out threshold))
                    return new RuleResult { Idle = false, Action = "none" };

                // User 是"无操作秒数 >= 阈值"才空闲；其余资源类都是"低于阈值"才空闲
                idle = (cond == "User") ? (metric >= threshold) : (metric < threshold);
            }
            // 无数值（Process/TimeWindow/旧树）：回退原布尔
            else if (values.ContainsKey(cond))
            {
                idle = values[cond];
            }
            else
            {
                return new RuleResult { Idle = false, Action = "none" };
            }

            // 收集失败叶子条件（仅供日志原因行；判定结果不受影响）
            if (!idle && !failed.Contains(cond))
                failed.Add(cond);

            return new RuleResult { Idle = idle, Action = "none" };
        }

        private static bool TryGetDouble(object obj, out double value)
        {
            value = 0;
            if (obj == null) return false;
            try
            {
                value = Convert.ToDouble(obj, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private RuleResult EvaluateOperator(Dictionary<string, object> node, Dictionary<string, bool> values, Dictionary<string, double> metrics, List<string> failed)
        {
            string op = "";
            if (node.ContainsKey("operator")) op = node["operator"] as string;
            else if (node.ContainsKey("op")) op = node["op"] as string;
            List<object> children = ToList(node.ContainsKey("children") ? node["children"] : null);

            if (children == null || children.Count == 0)
                return new RuleResult { Idle = true, Action = "none" };

            if (op == "AND")
            {
                // 全量求值收集失败叶子（纯布尔无副作用，与短路求值结果一致）
                bool allIdle = true;
                foreach (var child in children)
                {
                    var result = EvaluateNode(child as Dictionary<string, object>, values, metrics, failed);
                    if (!result.Idle)
                        allIdle = false;
                }
                return new RuleResult { Idle = allIdle, Action = "none" };
            }
            else if (op == "OR")
            {
                bool anyIdle = false;
                foreach (var child in children)
                {
                    var result = EvaluateNode(child as Dictionary<string, object>, values, metrics, failed);
                    if (result.Idle)
                        anyIdle = true;
                }
                return new RuleResult { Idle = anyIdle, Action = "none" };
            }
            else if (op == "NOT")
            {
                var result = EvaluateNode(children[0] as Dictionary<string, object>, values, metrics, failed);
                // NOT 反转导致的失败（子条件为空闲语义 true）没有对应源码行，不额外收集，由 MonitorEngine 兜底行覆盖
                return new RuleResult { Idle = !result.Idle, Action = "none" };
            }

            return new RuleResult { Idle = false, Action = "none" };
        }

        private RuleResult EvaluateControl(Dictionary<string, object> node, Dictionary<string, bool> values, Dictionary<string, double> metrics, List<string> failed)
        {
            // if 分支
            if (node.ContainsKey("condition") && node["condition"] != null)
            {
                var condResult = EvaluateNode(node["condition"] as Dictionary<string, object>, values, metrics, failed);
                if (condResult.Idle && node.ContainsKey("then") && node["then"] != null)
                {
                    return EvaluateNode(node["then"] as Dictionary<string, object>, values, metrics, failed);
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
                            var condResult = EvaluateNode(elif["condition"] as Dictionary<string, object>, values, metrics, failed);
                            if (condResult.Idle && elif.ContainsKey("then") && elif["then"] != null)
                            {
                                return EvaluateNode(elif["then"] as Dictionary<string, object>, values, metrics, failed);
                            }
                        }
                    }
                }
            }

            // else 分支
            if (node.ContainsKey("else") && node["else"] != null)
            {
                return EvaluateNode(node["else"] as Dictionary<string, object>, values, metrics, failed);
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
