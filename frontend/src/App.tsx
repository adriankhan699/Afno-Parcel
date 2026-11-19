import { Suspense, lazy } from 'react';
import { Navigate, Outlet, Route, Routes } from 'react-router';
import RequireAuth from './auth/RequireAuth';
import AdminLayout from './components/AdminLayout';
import AgentLayout from './components/AgentLayout';
import Layout from './components/Layout';
import BranchesAdminPage from './pages/admin/BranchesAdminPage';
import ScanLogPage from './pages/admin/ScanLogPage';
import StaffAdminPage from './pages/admin/StaffAdminPage';
import WaitingParcelsPage from './pages/admin/WaitingParcelsPage';
import WarehouseInventoryPage from './pages/admin/WarehouseInventoryPage';
import WarehousesAdminPage from './pages/admin/WarehousesAdminPage';
import { adminHomePath } from './lib/auth';
import { useAuth } from './auth/useAuth';
import BookParcelPage from './pages/BookParcelPage';
import BranchesPage from './pages/BranchesPage';
import HomePage from './pages/HomePage';
import LoginPage from './pages/LoginPage';
import NotFoundPage from './pages/NotFoundPage';
import ParcelDetailsPage from './pages/ParcelDetailsPage';
import ParcelLabelPage from './pages/ParcelLabelPage';
import ParcelsPage from './pages/ParcelsPage';
import PayReturnPage from './pages/PayReturnPage';
import PaymentReceiptPage from './pages/PaymentReceiptPage';
import PayTestGatewayPage from './pages/PayTestGatewayPage';
import ReceiptPage from './pages/ReceiptPage';
import RegisterPage from './pages/RegisterPage';
import ScanPage from './pages/ScanPage';
import TrackParcelPage from './pages/TrackParcelPage';

// The delivery pages (the agent's phone app and the delivery admin screens) are loaded when first opened.
const AgentPerformancePage = lazy(() => import('./pages/admin/AgentPerformancePage'));
const AgentsAdminPage = lazy(() => import('./pages/admin/AgentsAdminPage'));
const DeliveryAdminPage = lazy(() => import('./pages/admin/DeliveryAdminPage'));
// The finance pages and the agent's cash screen are loaded when first opened too.
const CodCashPage = lazy(() => import('./pages/admin/CodCashPage'));
const PaymentReportsPage = lazy(() => import('./pages/admin/PaymentReportsPage'));
const PaymentsAdminPage = lazy(() => import('./pages/admin/PaymentsAdminPage'));
// The notification pages (SMS and WhatsApp) are loaded when first opened too.
const NotificationsAdminPage = lazy(() => import('./pages/admin/NotificationsAdminPage'));
const AccountNotificationsPage = lazy(() => import('./pages/AccountNotificationsPage'));
const AgentCashPage = lazy(() => import('./pages/agent/AgentCashPage'));
const AgentAssignmentPage = lazy(() => import('./pages/agent/AgentAssignmentPage'));
const AgentHistoryPage = lazy(() => import('./pages/agent/AgentHistoryPage'));
const AgentMePage = lazy(() => import('./pages/agent/AgentMePage'));
const AgentTodayPage = lazy(() => import('./pages/agent/AgentTodayPage'));

function Loading() {
  return <p className="py-10 text-center text-sm text-slate-500">Loading…</p>;
}

/** The admin area opens on the finance pages for an accountant and on the branches for everyone else. */
function AdminHome() {
  const auth = useAuth();
  return <Navigate to={adminHomePath(auth.user?.roles).replace('/admin/', '')} replace />;
}

export default function App() {
  return (
    <Suspense fallback={<Loading />}>
    <Routes>
      {/* The delivery agent's phone app has its own slim layout and no links to the customer or staff pages. */}
      <Route
        path="agent"
        element={
          <RequireAuth needsAgent>
            <AgentLayout />
          </RequireAuth>
        }
      >
        <Route index element={<AgentTodayPage />} />
        <Route path="assignments/:id" element={<AgentAssignmentPage />} />
        <Route path="cash" element={<AgentCashPage />} />
        <Route path="history" element={<AgentHistoryPage />} />
        <Route path="me" element={<AgentMePage />} />
      </Route>
      <Route element={<Layout />}>
        <Route index element={<HomePage />} />
        <Route path="track" element={<TrackParcelPage />} />
        <Route path="track/:trackingNumber" element={<TrackParcelPage />} />
        <Route path="branches" element={<BranchesPage />} />
        <Route path="login" element={<LoginPage />} />
        <Route path="register" element={<RegisterPage />} />
        {/* Paying online. In test mode the "provider" page is a simulation inside this website; the return page lets the server verify. */}
        <Route path="pay/test/:provider/:reference" element={<PayTestGatewayPage />} />
        <Route path="pay/return/:provider" element={<PayReturnPage />} />
        <Route
          path="payments/:paymentId/receipt"
          element={
            <RequireAuth>
              <PaymentReceiptPage />
            </RequireAuth>
          }
        />
        <Route
          path="book"
          element={
            <RequireAuth needsBooking>
              <BookParcelPage />
            </RequireAuth>
          }
        />
        <Route
          path="parcels"
          element={
            <RequireAuth>
              <ParcelsPage />
            </RequireAuth>
          }
        />
        <Route
          path="parcels/:id"
          element={
            <RequireAuth>
              <ParcelDetailsPage />
            </RequireAuth>
          }
        />
        <Route
          path="parcels/:id/receipt"
          element={
            <RequireAuth>
              <ReceiptPage />
            </RequireAuth>
          }
        />
        <Route
          path="parcels/:id/label"
          element={
            <RequireAuth needsAdmin>
              <ParcelLabelPage />
            </RequireAuth>
          }
        />
        <Route
          path="account/notifications"
          element={
            <RequireAuth needsCustomer>
              <AccountNotificationsPage />
            </RequireAuth>
          }
        />
        <Route
          path="scan"
          element={
            <RequireAuth needsScan>
              <ScanPage />
            </RequireAuth>
          }
        />
        <Route
          path="admin"
          element={
            <RequireAuth needsAdmin>
              <AdminLayout />
            </RequireAuth>
          }
        >
          <Route index element={<AdminHome />} />
          {/* The network pages: not for an accountant-only login, who sees the finance pages below. */}
          <Route
            element={
              <RequireAuth needsNetworkView>
                <Outlet />
              </RequireAuth>
            }
          >
            <Route path="branches" element={<BranchesAdminPage />} />
            <Route path="warehouses" element={<WarehousesAdminPage />} />
            <Route path="warehouses/:id/inventory" element={<WarehouseInventoryPage />} />
            <Route path="staff" element={<StaffAdminPage />} />
            <Route path="inventory" element={<WaitingParcelsPage />} />
            <Route path="scan-log" element={<ScanLogPage />} />
            <Route
              path="agents"
              element={
                <RequireAuth needsDeliveryManagement>
                  <AgentsAdminPage />
                </RequireAuth>
              }
            />
            <Route
              path="delivery"
              element={
                <RequireAuth needsDeliveryManagement>
                  <DeliveryAdminPage />
                </RequireAuth>
              }
            />
            <Route
              path="agent-performance"
              element={
                <RequireAuth needsDeliveryManagement>
                  <AgentPerformancePage />
                </RequireAuth>
              }
            />
          </Route>
          {/* The finance pages: accountants, branch managers (their own branch) and super admins. */}
          <Route
            element={
              <RequireAuth needsFinance>
                <Outlet />
              </RequireAuth>
            }
          >
            <Route path="payments" element={<PaymentsAdminPage />} />
            <Route path="payment-reports" element={<PaymentReportsPage />} />
            <Route path="cod-cash" element={<CodCashPage />} />
          </Route>
          {/* SMS and WhatsApp notifications: super admins and branch managers (a branch manager only reads). Not accountants. */}
          <Route
            element={
              <RequireAuth needsNotifications>
                <Outlet />
              </RequireAuth>
            }
          >
            <Route path="notifications" element={<NotificationsAdminPage />} />
          </Route>
        </Route>
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
    </Suspense>
  );
}
