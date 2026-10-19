/** An answer a test holds back until it chooses to give it, so the page can be seen while it waits. */
type Held<T> = {
  promise: Promise<T>;
  resolve: (value: T) => void;
};

export function held<T>(): Held<T> {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
