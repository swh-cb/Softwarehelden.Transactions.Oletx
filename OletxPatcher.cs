﻿using HarmonyLib;
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Transactions;

namespace Softwarehelden.Transactions.Oletx
{
    /// <summary>
    /// The MSDTC patcher applies patches to the distributed transaction implementation in
    /// System.Transactions to make promotable transactions work with SQL servers in .NET 6.0. The
    /// patch would in theory also work for other data providers that support promotable single
    /// phase enlistment (PSPE) using the method Transaction.EnlistPromotableSinglePhase() where the
    /// database itself or an external service acts as a MSDTC superior transaction manager and can
    /// promote the internal transaction to a MSDTC transaction. The patch won't work when the data
    /// provider enlist a durable resource manager to participate in the transaction using
    /// Transaction.EnlistDurable() or Transaction.PromoteAndEnlistDurable(). However the data
    /// provider or the application can still enlist volatile resource managers using
    /// Transaction.EnlistVolatile() when data recovery is not required (or implemented) in case of
    /// a crash between the prepare and commit phase.
    /// </summary>
    /// <remarks>.NET issue: https://github.com/dotnet/runtime/issues/715</remarks>
    public static class OletxPatcher
    {
        /// <summary>
        /// The IL method patcher used to patch methods in System.Transactions.
        /// </summary>
        private static readonly Harmony MethodPatcher = new Harmony(nameof(OletxPatcher));

        /// <summary>
        /// A custom non-MSDTC promoter type for transaction enlistments.
        /// </summary>
        private static readonly Guid NonMsdtcPromoterType = new Guid("12ddadb4-dea6-4dea-a32a-51e8d9570b4e");

        /// <summary>
        /// The transaction manager that communicates with the MSDTC services.
        /// </summary>
        private static readonly OletxTransactionManager TransactionManager = new OletxTransactionManager();

        /// <summary>
        /// Applies the patches to System.Transactions.
        /// </summary>
        public static void Patch()
        {
            // MSDTC is still required on the machine. Therefore the patch can only be applied on Windows.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var getExportCookieMethod = typeof(TransactionInterop).GetMethod(
                    nameof(TransactionInterop.GetExportCookie),
                    BindingFlags.Static | BindingFlags.Public
                );

                var getNewExportCookieMethod = typeof(Patches).GetMethod(
                    nameof(Patches.GetExportCookie),
                    BindingFlags.Static | BindingFlags.Public
                );

                var enlistPromotableSinglePhaseMethod = typeof(Transaction).GetMethod(
                    nameof(Transaction.EnlistPromotableSinglePhase),
                    new Type[] { typeof(IPromotableSinglePhaseNotification), typeof(Guid) }
                );

                var setNonMsdtcPromoterTypeMethod = typeof(Patches).GetMethod(
                    nameof(Patches.SetNonMsdtcPromoterType),
                    BindingFlags.Static | BindingFlags.Public
                );

                MethodPatcher.Patch(getExportCookieMethod, new HarmonyMethod(getNewExportCookieMethod));
                MethodPatcher.Patch(enlistPromotableSinglePhaseMethod, new HarmonyMethod(setNonMsdtcPromoterTypeMethod));
            }
        }

        /// <summary>
        /// Patches for System.Transactions.
        /// </summary>
        private static class Patches
        {
            /// <summary>
            /// Returns a transaction cookie that is used to propagate/import a distributed
            /// transaction on a SQL server that wants to participate in the transaction.
            /// </summary>
            public static bool GetExportCookie(Transaction transaction, byte[] whereabouts, ref byte[] __result)
            {
                // Should the transaction be promoted using our custom promoter type?
                if (transaction.PromoterType == NonMsdtcPromoterType)
                {
                    __result = null;

                    // Promote the transaction on the MSDTC that owns the transaction (source
                    // MSDTC). System.Transactions calls only the Promote method of the promotable
                    // single phase notification implemented by the ADO.NET provider and do not call
                    // the MSDTC API directly which would result in a PlatformNotSupportedException
                    // on .NET 6.
                    byte[] propagationToken = transaction.GetPromotedToken();

                    if (propagationToken != null)
                    {
                        // The propagation token that the ADO.NET provider supplies is actually a
                        // MSDTC propagation token despite the non-MSDTC promoter type.

                        // However on-premises SQL servers do not support MSDTC propagation tokens
                        // but only transaction cookies for TDS propagate requests. So we must
                        // create a transaction cookie now that the SQL server can understand and import.

                        // Note that Azure SQL supports sending the propagation token directly in
                        // the TDS propagate request to make elastic transactions work natively in
                        // .NET 6 without querying the MSDTC API.

                        // Pull the promoted transaction from the source MSDTC to the local MSDTC
                        // (pull propagation)
                        var transactionShim = TransactionManager.ReceiveTransaction(propagationToken);

                        // Now push the transaction from the local MSDTC to the target MSDTC
                        // specified in the whereabouts (push propagation)
                        __result = TransactionManager.GetExportCookie(transactionShim, whereabouts);
                    }

                    // We got an export cookie, so do not call the original method
                    return false;
                }
                else
                {
                    // Call the original method since we cannot handle the promoter type of the transaction
                    return true;
                }
            }

            /// <summary>
            /// Sets a non-MSDTC promoter type for the method <see
            /// cref="Transaction.EnlistPromotableSinglePhase(IPromotableSinglePhaseNotification, Guid)"/>.
            /// </summary>
            public static void SetNonMsdtcPromoterType(ref Guid promoterType)
            {
                // Set a custom promoter type for all MSDTC promotable transactions. If the promoter
                // type is not MSDTC then the transaction state machine in System.Transactions
                // cannot assume that the propagation token is a MSDTC propagation token. Therefore
                // the framework also does not attempt to create a distributed transaction and no
                // PlatformNotSupportedException will be thrown when the transaction is being
                // promoted. Elastic database transactions in Azure SQL work basically the same way.
                if (promoterType == TransactionInterop.PromoterTypeDtc)
                {
                    promoterType = NonMsdtcPromoterType;
                }
            }
        }
    }
}