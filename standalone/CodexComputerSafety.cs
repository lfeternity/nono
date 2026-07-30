using System;

namespace NoNoStandalone
{
    internal static class CodexComputerSafety
    {
        private static readonly string[] PaymentSignals = new string[]
        {
            "支付", "付款", "收银台", "结账", "购买", "下单", "转账", "汇款", "充值", "提现",
            "支付宝", "微信支付", "银联", "信用卡", "借记卡", "银行卡",
            "payment", "checkout", "purchase", "buy now", "place order", "pay now", "transfer money",
            "alipay", "wechat pay", "paypal", "stripe", "unionpay", "credit card", "debit card", "cvv"
        };

        private static readonly string[] FileDeletionSignals = new string[]
        {
            "删除文件", "删掉文件", "移入回收站", "清空回收站", "永久删除", "粉碎文件",
            "delete file", "remove file", "trash file", "empty recycle bin", "permanently delete", "shred file"
        };

        private static readonly string[] DeletionActionSignals = new string[]
        {
            "删除", "删掉", "移除", "清空", "粉碎", "delete", "remove", "trash", "erase", "purge", "wipe", "shred"
        };

        private static readonly string[] CredentialSignals = new string[]
        {
            "密码", "口令", "验证码", "密钥", "令牌", "凭据", "助记词", "私钥",
            "password", "passcode", "otp", "secret", "token", "credential", "seed phrase", "private key"
        };

        private static readonly string[] ForbiddenDeletionToolSignals = new string[]
        {
            "delete", "remove", "trash", "unlink", "erase", "purge", "wipe", "shred"
        };

        public static bool ContainsPaymentIntent(string value)
        {
            return ContainsAny(value, PaymentSignals);
        }

        public static bool ContainsFileDeletionIntent(string value)
        {
            return ContainsAny(value, FileDeletionSignals) || ContainsAny(value, DeletionActionSignals);
        }

        public static bool ContainsCredentialSignal(string value)
        {
            return ContainsAny(value, CredentialSignals);
        }

        public static bool IsForbiddenDeletionTool(string tool)
        {
            string value = (tool ?? "").Trim();
            return ContainsAny(value, ForbiddenDeletionToolSignals);
        }

        public static string GetForbiddenGoalReason(string goal)
        {
            if (ContainsPaymentIntent(goal))
            {
                return "宠物不执行支付、付款、购买、下单、转账或其他资金操作";
            }

            if (ContainsFileDeletionIntent(goal))
            {
                return "宠物不删除文件，也不把文件移入回收站";
            }

            return "";
        }

        public static void EnsureNoPaymentIntent(string value)
        {
            if (ContainsPaymentIntent(value))
            {
                throw new InvalidOperationException("已阻止支付、付款、购买、下单、转账或其他资金操作。");
            }
        }

        private static bool ContainsAny(string value, string[] words)
        {
            string source = value ?? "";
            for (int i = 0; i < words.Length; i++)
            {
                if (source.IndexOf(words[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
