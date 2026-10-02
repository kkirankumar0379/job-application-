import { Component, type ErrorInfo, type ReactNode } from 'react';

type State = { error: Error | null };

/** Shows the error and a reload button instead of a blank page when a component crashes. */
export default class ErrorBoundary extends Component<{ children: ReactNode }, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('JobAgent crashed:', error, info.componentStack);
  }

  render() {
    if (!this.state.error) return this.props.children;
    return (
      <main>
        <section className="card">
          <h2>Something went wrong on this page</h2>
          <p className="muted">Reloading usually fixes it. Your profile and jobs are saved on the server.</p>
          <pre className="error-detail">{this.state.error.message}</pre>
          <button className="primary" onClick={() => window.location.reload()}>Reload</button>
        </section>
      </main>
    );
  }
}
