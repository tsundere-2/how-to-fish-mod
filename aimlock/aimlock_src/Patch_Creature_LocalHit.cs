using System;
using HarmonyLib;
using UnityEngine;

namespace aimlock
{
    /// <summary>
    /// 方式A：让子弹“自动打到头部”。
    /// 游戏在【射击者本地客户端】用 point 计算爆头：
    ///   if (rangedHit &amp;&amp; transform.InverseTransformPoint(point).z &gt; HeadPos) damage *= HeadShotDamageMulti;
    /// 之后通过 ServerRpc(HitCreature) 把 damage 发给服务器，服务器不校验命中点。
    /// 因此只需在命中点进入 LocalHit 之前，把它的本地 Z 推到 HeadPos 之后即可 100% 判定为爆头。
    /// 本补丁还顺带把命中特效/伤害数字也显示在头部（更自然）。
    /// </summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.LocalHit))]
    internal static class Patch_Creature_LocalHit
    {
        private static void Prefix(Creature __instance, ref Vector3 point, Player player, int damage, bool rangedHit)
        {
            try
            {
                FishAimLock.OnCreatureLocalHit(__instance, ref point, rangedHit);
            }
            catch (Exception ex)
            {
                if (FishAimLock.Log != null)
                    FishAimLock.Log.LogError("[aimlock] LocalHit 补丁异常: " + ex);
            }
        }
    }
}
