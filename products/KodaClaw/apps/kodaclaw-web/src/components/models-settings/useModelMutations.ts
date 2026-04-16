import { useMutation, useQueryClient } from "@tanstack/react-query";
import {
  createAccountModel,
  createProviderAccount,
  deleteAccountModel,
  deleteProviderAccount,
  setDefaultAccountModel,
  updateAccountModel,
  updateProviderAccount,
} from "../../lib/api";
import { queryKeys } from "../../lib/queryKeys";

/**
 * Centralized TanStack `useMutation` hooks for the provider-account page.
 *
 * Every mutation invalidates the `providerAccounts` query on success, so the
 * caller never has to remember to refresh the list. The hook also exposes a
 * single `isMutating` flag (union of all pending states) and a `lastError`
 * string (message of the most recent failed mutation) so the consumer can
 * render a single spinner / error banner without juggling 7 states.
 */
export function useModelMutations() {
  const queryClient = useQueryClient();
  const invalidateAccounts = () =>
    queryClient.invalidateQueries({ queryKey: queryKeys.providerAccounts });

  const createEndpoint = useMutation({
    mutationFn: (request: Parameters<typeof createProviderAccount>[0]) =>
      createProviderAccount(request),
    onSuccess: () => void invalidateAccounts(),
  });

  const updateEndpoint = useMutation({
    mutationFn: (args: { accountId: string; request: Parameters<typeof updateProviderAccount>[1] }) =>
      updateProviderAccount(args.accountId, args.request),
    onSuccess: () => void invalidateAccounts(),
  });

  const createModel = useMutation({
    mutationFn: (args: { accountId: string; request: Parameters<typeof createAccountModel>[1] }) =>
      createAccountModel(args.accountId, args.request),
    onSuccess: () => void invalidateAccounts(),
  });

  const updateModel = useMutation({
    mutationFn: (args: {
      accountId: string;
      modelId: string;
      request: Parameters<typeof updateAccountModel>[2];
    }) => updateAccountModel(args.accountId, args.modelId, args.request),
    onSuccess: () => void invalidateAccounts(),
  });

  const deleteEndpoint = useMutation({
    mutationFn: (accountId: string) => deleteProviderAccount(accountId),
    onSuccess: () => void invalidateAccounts(),
  });

  const deleteModel = useMutation({
    mutationFn: (args: { accountId: string; modelId: string }) =>
      deleteAccountModel(args.accountId, args.modelId),
    onSuccess: () => void invalidateAccounts(),
  });

  const setDefault = useMutation({
    mutationFn: (args: { accountId: string; modelId: string }) =>
      setDefaultAccountModel(args.accountId, args.modelId),
    onSuccess: () => void invalidateAccounts(),
  });

  const all = [
    createEndpoint,
    updateEndpoint,
    createModel,
    updateModel,
    deleteEndpoint,
    deleteModel,
    setDefault,
  ] as const;

  const isMutating = all.some((m) => m.isPending);
  const lastError =
    (all.find((m) => m.error)?.error as Error | undefined)?.message ?? null;

  return {
    createEndpoint,
    updateEndpoint,
    createModel,
    updateModel,
    deleteEndpoint,
    deleteModel,
    setDefault,
    isMutating,
    lastError,
  };
}

export type ModelMutations = ReturnType<typeof useModelMutations>;
