import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export type AuthUser = {
  sellerId: string;
  sellerKey: string;
  displayName: string;
  email: string;
  role: 'Admin' | 'Seller';
  verticalsWhitelist: string[];
};

type State = {
  token: string | null;
  user: AuthUser | null;
  setAuth: (token: string, user: AuthUser) => void;
  logout: () => void;
};

export const useAuthStore = create<State>()(
  persist(
    (set) => ({
      token: null,
      user: null,
      setAuth: (token, user) => set({ token, user }),
      logout: () => set({ token: null, user: null })
    }),
    { name: 'saleshub-auth' }
  )
);

export const isAdmin = (u: AuthUser | null) => u?.role === 'Admin';
