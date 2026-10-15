import type { AuthState, AuthUser, UserRole } from "../types/api";
import { getJson, sendJson } from "./http";

export function getAuthState(signal?: AbortSignal): Promise<AuthState> {
  return getJson<AuthState>("/api/auth/state", signal);
}

export async function setup(username: string, password: string): Promise<AuthUser> {
  return (await sendJson<AuthUser>("POST", "/api/auth/setup", { username, password }))!;
}

export async function login(username: string, password: string): Promise<AuthUser> {
  return (await sendJson<AuthUser>("POST", "/api/auth/login", { username, password }))!;
}

export async function logout(): Promise<void> {
  await sendJson("POST", "/api/auth/logout");
}

export async function changePassword(currentPassword: string, newPassword: string): Promise<void> {
  await sendJson("POST", "/api/auth/password", { currentPassword, newPassword });
}

export async function listUsers(signal?: AbortSignal): Promise<AuthUser[]> {
  return (await getJson<{ users: AuthUser[] }>("/api/users", signal)).users;
}

export async function createUser(username: string, password: string, role: UserRole): Promise<AuthUser> {
  return (await sendJson<AuthUser>("POST", "/api/users", { username, password, role }))!;
}

export async function updateUser(id: string, change: { role?: UserRole; password?: string }): Promise<void> {
  await sendJson("PATCH", `/api/users/${encodeURIComponent(id)}`, change);
}

export async function deleteUser(id: string): Promise<void> {
  await sendJson("DELETE", `/api/users/${encodeURIComponent(id)}`);
}
