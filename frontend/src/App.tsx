import { Navigate, Route, Routes } from 'react-router-dom';
import { isAdmin, useAuthStore } from './lib/auth';
import Login from './pages/Login';
import Layout from './components/Layout';
import MyLeads from './pages/MyLeads';
import LeadsImport from './pages/LeadsImport';
import LeadDetail from './pages/LeadDetail';
import Pool from './pages/Pool';
import MyDashboard from './pages/MyDashboard';
import Connect from './pages/Connect';
import AdminDashboard from './pages/AdminDashboard';
import Sellers from './pages/Sellers';
import SellerDetail from './pages/SellerDetail';
import SellerZones from './pages/SellerZones';
import Products from './pages/Products';
import Pipeline from './pages/Pipeline';
import Competitors from './pages/Competitors';
import Trends from './pages/Trends';
import MapPage from './pages/Map';
import Conversations from './pages/Conversations';

export default function App() {
  const user = useAuthStore((s) => s.user);

  if (!user) {
    return (
      <Routes>
        <Route path="/login" element={<Login />} />
        <Route path="*" element={<Navigate to="/login" replace />} />
      </Routes>
    );
  }

  return (
    <Routes>
      <Route element={<Layout />}>
        <Route path="/" element={<Navigate to={isAdmin(user) ? '/admin' : '/dashboard'} replace />} />
        <Route path="/dashboard" element={<MyDashboard />} />
        <Route path="/leads" element={<MyLeads />} />
        <Route path="/leads/import" element={<LeadsImport />} />
        <Route path="/leads/:id" element={<LeadDetail />} />
        <Route path="/pool" element={<Pool />} />
        <Route path="/connect" element={<Connect />} />
        <Route path="/conversations" element={<Conversations />} />
        <Route path="/map" element={<MapPage />} />
        {isAdmin(user) && (
          <>
            <Route path="/admin" element={<AdminDashboard />} />
            <Route path="/sellers" element={<Sellers />} />
            <Route path="/sellers/:id/zones" element={<SellerZones />} />
            <Route path="/admin/sellers/:id" element={<SellerDetail />} />
            <Route path="/products" element={<Products />} />
            <Route path="/pipeline" element={<Pipeline />} />
            <Route path="/competitors" element={<Competitors />} />
            <Route path="/trends" element={<Trends />} />
          </>
        )}
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
    </Routes>
  );
}
