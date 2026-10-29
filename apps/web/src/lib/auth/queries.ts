import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  fetchBootstrapStatus,
  fetchCurrentSession,
  fetchActiveSessions,
  fetchRecoveryCodes,
  postChangePassword,
  postChangeUsername,
  postConfirmRecoveryCodes,
  postNewRecoveryCodes,
  postRecoveryCode,
  postRecoveryPassword,
  postSetTheme,
  fetchMe,
  postBootstrap,
  postLogin,
  postLogout,
  postRevokeOtherSessions,
  postRevokeSession,
} from "../api/auth-api";
import type { UserPublic } from "../api/types";
import { bringBackRecoveryPrompt } from "./recovery-prompt";
import { clearLoginSucceeded, markLoginSucceeded } from "./session-kept";
import {
  clearSignedIn,
  finishChangingPassword,
  finishSigningOut,
  markSignedIn,
  startChangingPassword,
  startSigningOut,
} from "./signed-in-before";
import { clearSignOutFailed, markSignOutFailed } from "./sign-out-failed";
import { activityKeys } from "../activity/query-keys";
import { authKeys } from "./query-keys";

export function useMeQuery() {
  return useQuery({
    queryKey: authKeys.me,
    queryFn: fetchMe,
    retry: false,
  });
}

export function useBootstrapStatusQuery() {
  return useQuery({
    queryKey: authKeys.bootstrap,
    queryFn: fetchBootstrapStatus,
    retry: 1,
  });
}

export function useCurrentSessionQuery() {
  return useQuery({
    queryKey: authKeys.session,
    queryFn: fetchCurrentSession,
    retry: false,
  });
}

export function useActiveSessionsQuery(enabled = true) {
  return useQuery({
    queryKey: authKeys.sessions,
    queryFn: fetchActiveSessions,
    retry: false,
    enabled,
  });
}

export function useRevokeOtherSessionsMutation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: postRevokeOtherSessions,
    onSuccess: async () => {
      await Promise.all([
        qc.invalidateQueries({ queryKey: authKeys.sessions }),
        qc.invalidateQueries({ queryKey: activityKeys.recent }),
      ]);
    },
  });
}

export function useRevokeSessionMutation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (sessionId: string) => postRevokeSession(sessionId),
    onSuccess: async () => {
      await Promise.all([
        qc.invalidateQueries({ queryKey: authKeys.sessions }),
        qc.invalidateQueries({ queryKey: activityKeys.recent }),
      ]);
    },
  });
}

export function useLoginMutation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({
      username,
      password,
      trustedDevice,
    }: {
      username: string;
      password: string;
      trustedDevice: boolean;
    }) => postLogin(username, password, trustedDevice),
    onSuccess: (data) => {
      // Remember that the server accepted us, so a 401 moments later can be reported as a
      // rejected cookie rather than a silent bounce back to the form (#453).
      markLoginSucceeded();
      markSignedIn();
      finishChangingPassword();
      bringBackRecoveryPrompt();
      // Anonymous /me is cached as `null` (401). Hydrate from the login response — do not invalidate
      // /me here: an immediate refetch can run before the session cookie is visible to fetch(), get
      // 401, and overwrite this cache back to null (E2E/CI flake).
      qc.setQueryData(authKeys.me, data.user);
      void qc.invalidateQueries({ queryKey: authKeys.bootstrap });
      void qc.invalidateQueries({ queryKey: authKeys.session });
      void qc.invalidateQueries({ queryKey: authKeys.recoveryCodes });
      void qc.invalidateQueries({ queryKey: activityKeys.recent });
    },
  });
}

export function useLogoutMutation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () =>
      postLogout(async () => {
        // Show the sign-in page only once the logout request is about to go out, so it cannot be left unsent.
        await qc.cancelQueries({ queryKey: authKeys.me });
        await qc.cancelQueries({ queryKey: authKeys.session });
        qc.setQueryData(authKeys.me, null);
        qc.setQueryData(authKeys.session, null);
      }),
    onMutate: () => {
      clearSignOutFailed();
      startSigningOut();
    },
    onSuccess: clearSignedIn,
    onError: markSignOutFailed,
    onSettled: () => {
      finishSigningOut();
      void qc.invalidateQueries({ queryKey: authKeys.me });
      void qc.invalidateQueries({ queryKey: authKeys.session });
      void qc.invalidateQueries({ queryKey: authKeys.bootstrap });
      void qc.invalidateQueries({ queryKey: activityKeys.recent });
    },
  });
}

export function useBootstrapMutation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({
      username,
      password,
      setupCode,
    }: {
      username: string;
      password: string;
      setupCode?: string;
    }) => postBootstrap(username, password, setupCode),
    onSuccess: (data) => {
      // Creating the account also signs it in (#704): hydrate /me from the response, the same way
      // sign-in does, rather than invalidating it and racing the new cookie's first request.
      markLoginSucceeded();
      markSignedIn();
      qc.setQueryData(authKeys.me, data.user);
      void qc.invalidateQueries({ queryKey: authKeys.bootstrap });
      void qc.invalidateQueries({ queryKey: authKeys.session });
      void qc.invalidateQueries({ queryKey: activityKeys.recent });
    },
  });
}

export function useChangeUsernameMutation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({
      currentPassword,
      newUsername,
    }: {
      currentPassword: string;
      newUsername: string;
    }) => postChangeUsername(currentPassword, newUsername),
    onSuccess: () => {
      // The session is untouched by a rename, so only the displayed identity needs refreshing.
      void qc.invalidateQueries({ queryKey: authKeys.me });
      void qc.invalidateQueries({ queryKey: activityKeys.recent });
    },
  });
}

export function useSetThemeMutation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: postSetTheme,
    onSuccess: (data) => {
      // The switch (or the account-theme sync adopting a local choice) already applied the theme
      // instantly; this only carries it to the cached account so every other screen reading
      // /auth/me agrees with it too.
      qc.setQueryData(authKeys.me, (previous: UserPublic | null | undefined) =>
        previous ? { ...previous, app_theme: data.app_theme } : previous,
      );
    },
  });
}

export function useChangePasswordMutation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({
      currentPassword,
      newPassword,
    }: {
      currentPassword: string;
      newPassword: string;
    }) => postChangePassword(currentPassword, newPassword),
    onMutate: () => {
      // The sign-in may end before the answer arrives, and it must not read as a cookie the browser refused.
      clearLoginSucceeded();
      startChangingPassword();
    },
    onError: finishChangingPassword,
    onSuccess: () => {
      // The server has ended every sign-in, this one included, so no one is left to ask who is signed in: the page
      // goes to the sign-in page, which says the password was changed.
      qc.setQueryData(authKeys.me, null);
      qc.setQueryData(authKeys.session, null);
    },
  });
}

/** How many recovery codes are left and whether the set was saved. Off until a signed-in screen asks. */
export function useRecoveryCodesQuery(enabled = true) {
  return useQuery({
    queryKey: authKeys.recoveryCodes,
    queryFn: fetchRecoveryCodes,
    retry: false,
    enabled,
  });
}

/** Makes a new set after the current password; the codes come back once and are kept nowhere. */
export function useNewRecoveryCodesMutation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: postNewRecoveryCodes,
    onSuccess: (data) => {
      qc.setQueryData(authKeys.recoveryCodes, {
        remaining: data.remaining,
        confirmed: data.confirmed,
      });
    },
  });
}

export function useConfirmRecoveryCodesMutation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: postConfirmRecoveryCodes,
    onSuccess: (data) => {
      qc.setQueryData(authKeys.recoveryCodes, {
        remaining: data.remaining,
        confirmed: data.confirmed,
      });
    },
  });
}

export function useRecoveryCodeMutation() {
  return useMutation({ mutationFn: postRecoveryCode });
}

export function useRecoveryPasswordMutation() {
  return useMutation({
    mutationFn: ({
      recoveryToken,
      newPassword,
    }: {
      recoveryToken: string;
      newPassword: string;
    }) => postRecoveryPassword(recoveryToken, newPassword),
  });
}
